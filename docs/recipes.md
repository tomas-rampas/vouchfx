# vouchfx Recipes: Common Patterns and Examples

This document collects task-oriented recipes for common testing scenarios in vouchfx. Each recipe is a self-contained, runnable `.e2e.yaml` file (or pattern) with explanation.

**Table of contents:**
1. [Seeding with SQL fixtures](#seeding-with-sql-fixtures)
2. [Test doubles with WireMock](#test-doubles-with-wiremock)
3. [Injecting secrets from environment variables](#injecting-secrets-from-environment-variables)
4. [Injecting secrets from Vault](#injecting-secrets-from-vault)
5. [Multi-step state threading with capture and placeholders](#multi-step-state-threading-with-capture-and-placeholders)
6. [Engine-owned polling with verifyMode: RETRY](#engine-owned-polling-with-verifymode-retry)
7. [CI integration with GitHub Actions](#ci-integration-with-github-actions)
8. [CI integration with GitLab CI](#ci-integration-with-gitlab-ci)

For the exhaustive reference on every step type's fields, see [`docs/language-reference.md`](language-reference.md). For the full DSL specification, see [`docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md`](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md).

---

## Seeding with SQL fixtures

**When to use:** You need reference data or an initialised schema before any test step runs — e.g., a lookup table, base records, or test-specific tables.

**How it works:** The `environment.seed` block declares SQL files to execute against each managed dependency **after the topology is healthy but before step 1 runs**. Seed failures produce an `EnvironmentError` verdict (infrastructure problem), not a test failure, so a broken fixture never masks a system defect.

### Example: reference data seed

Create a fixture file `fixtures/reference.sql`:

```sql
CREATE TABLE region_codes (code TEXT PRIMARY KEY, region_name TEXT NOT NULL);
INSERT INTO region_codes (code, region_name) VALUES ('emea', 'EMEA');
INSERT INTO region_codes (code, region_name) VALUES ('apac', 'APAC');
INSERT INTO region_codes (code, region_name) VALUES ('amer', 'Americas');

CREATE TABLE users (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
```

Then reference it in your `.e2e.yaml`:

```yaml
metadata:
  name: user-registration-with-regions
  owner: team-a
  tags: [smoke, user-mgmt]
  description: Create a user and verify it is assigned to the correct region via a seeded lookup table.

environment:
  services:
    user-api:
      image: myco/user-service:latest
      httpPort: 8080
  dependencies:
    users-db:
      type: postgres
  seed:
    users-db:
      sql: [ "fixtures/reference.sql" ]

steps:
  - id: register-user-emea
    type: http.rest
    target: user-api
    method: POST
    path: /users
    body:
      name: "Alice"
      region_code: "emea"
    expect:
      status: 201
    capture:
      user_id: "$.id"

  - id: assert-user-region
    type: db-assert.postgres
    target: users-db
    query: >-
      SELECT u.name, r.region_name
      FROM users u
      JOIN region_codes r ON r.code = u.region_code
      WHERE u.id = @user_id
    parameters:
      user_id: "{user_id}"
    expect:
      rowCount: 1
      row:
        region_name: "EMEA"
```

**Key points:**

- The `seed.users-db.sql` array lists **relative paths** to SQL fixture files. Paths are resolved relative to the scenario file's directory (no CLI override exists for the seed base directory).
- **All files are executed in order** against the named dependency before step 1 runs.
- Seed SQL can contain DDL (CREATE TABLE) and DML (INSERT, UPDATE). Multi-statement files are split and executed serially.
- **Seed failure = EnvironmentError**, not a test failure. If `fixtures/reference.sql` fails to execute, the whole run aborts with `EnvironmentError`, clearly signalling infrastructure trouble.
- The reproducibility envelope records the **content hash** of each seeded fixture (see `docs/01` §4 and §14), never its raw SQL.

---

## Test doubles with WireMock

**When to use:** A real dependency (external payment gateway, third-party API, service not yet built) cannot or should not be exercised. You want to stub its behaviour without writing code.

**How it works:** Test doubles are **ordinary containers** in your `environment.services` section. WireMock (or Mountebank, or any HTTP-stubbing tool) runs as a container, exposes a management API and a stub API, and your test steps call it just like any real service. The test logic never knows or cares whether it is talking to a stub or the real service — the seam is in the environment declaration, exactly where it belongs.

### Example: stubbed payment gateway

Create a WireMock stub configuration file `stubs/payment-gateway.json`:

```json
{
  "mappings": [
    {
      "request": {
        "method": "POST",
        "urlPattern": "/payments/process"
      },
      "response": {
        "status": 200,
        "jsonBody": {
          "transactionId": "txn-12345",
          "status": "approved",
          "amount": 99.99
        }
      }
    },
    {
      "request": {
        "method": "POST",
        "urlPattern": "/payments/decline"
      },
      "response": {
        "status": 400,
        "jsonBody": {
          "error": "card_declined",
          "reason": "Insufficient funds"
        }
      }
    }
  ]
}
```

Then declare WireMock as a service and use it in your steps:

```yaml
metadata:
  name: payment-processing-integration
  owner: team-payments
  tags: [integration, payment]
  description: Submit a payment request to a stubbed gateway and verify the response is recorded in the database.

environment:
  services:
    # The application being tested
    checkout-api:
      image: myco/checkout-service:latest
      httpPort: 8080
      env:
        PAYMENT_GATEWAY_URL: "http://payment-gateway:8080"

    # The stubbed payment gateway (WireMock running in a container)
    payment-gateway:
      image: wiremock/wiremock:3.0.1
      httpPort: 8080
      # WireMock reads stub mappings from /home/wiremock; mount the config as a volume
      # (This example assumes you've copied stubs/ into the container or a volume)

  dependencies:
    checkout-db:
      type: postgres

steps:
  - id: submit-payment
    type: http.rest
    target: checkout-api
    method: POST
    path: /checkout/pay
    body:
      amount: 99.99
      currency: "GBP"
      card_token: "tok_visa"
    expect:
      status: 200
    capture:
      transaction_id: "$.transactionId"

  - id: verify-transaction-recorded
    type: db-assert.postgres
    target: checkout-db
    query: >-
      SELECT txn_id, status, amount
      FROM transactions
      WHERE txn_id = @txn_id
    parameters:
      txn_id: "{transaction_id}"
    expect:
      rowCount: 1
      row:
        status: "approved"
        amount: 99.99
```

**Key points:**

- WireMock (or any HTTP double) is declared as a service in `environment.services`, not special-cased.
- `http.rest` steps call the double and assert on its responses just as they would a real service.
- **Swapping the double for the real service later requires only a change to the `environment` declaration**; the step logic stays identical.
- Pre-configure the double's stub mappings at container startup (via volume mount, environment variable config, or an init script).
- The platform ships no built-in mocking feature; doubles are visible, deliberate entries in your environment.

---

## Injecting secrets from environment variables

**When to use:** You need to pass a credential (API key, bearer token, password) into a step without embedding it in source control or logs.

**How it works:** A `${secret:env/VAR_NAME}` reference is resolved at **step-execution time** (never at compile time), replaced with the value of the environment variable `VAR_NAME`, and then redacted from all output and reports (displayed as `(redacted)` in logs and the HTML report). The resolved value is **never compiled into the C# IL**, never persists in the reproducibility envelope, and **never appears in `--events` JSON output** (only the reference path appears).

### Example: API key in a header

```yaml
metadata:
  name: third-party-api-integration
  owner: team-integrations
  tags: [integration, external-api]
  description: Call a third-party API with a secret bearer token injected from the environment.

environment:
  services:
    third-party-api:
      image: myco/api-gateway:latest
      httpPort: 8080
      env:
        # The gateway forwards the Authorization header to the real external API
        EXTERNAL_API_URL: "https://api.external-vendor.com"

steps:
  - id: fetch-data-with-auth
    type: http.rest
    target: third-party-api
    method: GET
    path: /data
    headers:
      # The reference ${secret:env/EXTERNAL_API_KEY} is resolved at execution time
      # to the value of the EXTERNAL_API_KEY environment variable.
      Authorization: "Bearer ${secret:env/EXTERNAL_API_KEY}"
    expect:
      status: 200
    capture:
      result: "$.data"

  - id: verify-result
    type: script.csharp
    code: |
      var result = (string)Vars["result"];
      if (string.IsNullOrWhiteSpace(result))
        throw new Exception("Expected non-empty result, got: " + result);
      // The secret value is NEVER visible here — only placeholders and references
      // appear in the captured state. A script that throws will reveal ONLY
      // the captured value, never the resolved secret.
```

**To run this test:**

Set the environment variable before invoking the CLI:

```bash
export EXTERNAL_API_KEY="sk_test_abc123def456"
vouchfx run tests/integrations
```

Or on Windows:

```powershell
$env:EXTERNAL_API_KEY = "sk_test_abc123def456"
src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.exe run tests/integrations
```

**Key points:**

- **Secret references are resolved at execution time**, not compile time — the resolved value never enters source code, logs, or reports.
- The `${secret:env/VAR_NAME}` reference appears in output **only with the value redacted**: `${secret:env/EXTERNAL_API_KEY} (redacted)`.
- The **resolved value never appears anywhere** — not in terminal output, not in `--events` JSON Lines, not in the HTML report.
- A `script.csharp` step that throws an exception and reveals a secret in its message will expose that value in `--events` output (because observations are persisted verbatim); authors must avoid embedding secrets in exception messages.
- The reproducibility envelope records a **hash of the reference path** (`env/EXTERNAL_API_KEY`), never the resolved value — this supports reproducibility without baking secrets into the record.

---

## Injecting secrets from Vault

**When to use:** Your secrets are stored in a centralized Vault (HashiCorp Vault, AWS Secrets Manager, or similar) and you want vouchfx to resolve them at execution time.

**How it works:** A `${secret:vault/path/to/secret}` reference is resolved by consulting the configured Vault backend at execution time. The Vault URL and authentication credentials are configured via environment variables (`VOUCHFX_VAULT_ADDR`, `VOUCHFX_VAULT_TOKEN`, etc., see `docs/01` §17 for full details).

### Example: secret from Vault

```yaml
metadata:
  name: vault-secret-integration
  owner: team-security
  tags: [integration, secrets]
  description: Retrieve a database credential from Vault and use it in a connection string.

environment:
  dependencies:
    secure-db:
      type: postgres

steps:
  - id: connect-with-vault-secret
    type: script.csharp
    code: |
      // The Vars.Secrets property is the execution-time secret accessor.
      // Call Vars.Secrets.Resolve("vault/database/prod") to get a SecretString.
      // 
      // To use the secret, call Reveal() to get the raw value:
      var dbSecret = Vars.Secrets.Resolve("vault/database/prod");
      var revealed = dbSecret.Reveal();
      var connStr = $"Host=localhost;Username=db_user;Password={revealed};Database=prod";
      // The revealed value is valid only at the injection sink; never write it back
      // into Vars or any logged/serialised structure.
```

Then configure Vault credentials before running:

```bash
export VOUCHFX_VAULT_ADDR="https://vault.example.com"
export VOUCHFX_VAULT_TOKEN="s.xxxxxxxxx"
vouchfx run tests/secure
```

**Key points:**

- Vault sources are configured at runtime via environment variables (`VOUCHFX_VAULT_ADDR`, `VOUCHFX_VAULT_TOKEN`, etc.).
- A `${secret:vault/path/to/secret}` reference is resolved from Vault at step-execution time.
- In `script.csharp` steps, a resolved secret is available as a `SecretString` (see `docs/01` §17) — a type that prevents accidental logging or serialization of the value.
- The reference path (e.g., `vault/database/prod`) appears in output and the reproducibility envelope; the resolved value does not.
- For the full Vault configuration and API, see `docs/01` §17 (Secrets).

---

## Multi-step state threading with capture and placeholders

**When to use:** A later step depends on data produced by an earlier step — e.g., create a resource, capture its ID, then query or delete it.

**How it works:** The `capture` field on a step (using JSONPath, XPath, or other extractors) writes values from the step's result into a shared dictionary (`Vars`). Later steps can reference captured values using `{placeholder}` syntax. State threads forward through the entire scenario.

### Example: create → verify → update → assert

```yaml
metadata:
  name: multi-step-resource-lifecycle
  owner: team-orders
  tags: [integration, lifecycle]
  description: Create a resource, capture its ID, then verify, update, and assert it via a chain of steps.

environment:
  services:
    order-api:
      image: myco/order-service:latest
      httpPort: 8080
  dependencies:
    orders-db:
      type: postgres
  seed:
    orders-db:
      sql: [ "fixtures/init.sql" ]

variables:
  initial_status: "pending"
  updated_status: "shipped"

steps:
  # Step 1: Create an order and capture its ID
  - id: create-order
    type: http.rest
    target: order-api
    method: POST
    path: /orders
    body:
      customer_id: "cust_001"
      items: [ { sku: "ABC123", qty: 2 } ]
      total: 49.99
    expect:
      status: 201
    capture:
      # JSONPath extracts the 'id' field from the JSON response
      order_id: "$.id"
      # Multiple captures from the same response
      created_at: "$.created_at"

  # Step 2: Verify the order in the database (using the captured ID)
  - id: verify-order-created
    type: db-assert.postgres
    target: orders-db
    query: >-
      SELECT id, customer_id, status, total
      FROM orders
      WHERE id = @order_id
    parameters:
      # The placeholder {order_id} resolves to the captured value from step 1
      order_id: "{order_id}"
    expect:
      rowCount: 1
      row:
        customer_id: "cust_001"
        status: "{initial_status}"
        total: 49.99

  # Step 3: Update the order status via the API
  - id: ship-order
    type: http.rest
    target: order-api
    method: PATCH
    path: "/orders/{order_id}"
    body:
      status: "{updated_status}"
    expect:
      status: 200
    capture:
      shipped_at: "$.shipped_at"

  # Step 4: Assert the database reflects the update
  - id: verify-order-updated
    type: db-assert.postgres
    target: orders-db
    query: >-
      SELECT id, status, updated_at
      FROM orders
      WHERE id = @order_id
    parameters:
      order_id: "{order_id}"
    expect:
      rowCount: 1
      row:
        status: "{updated_status}"
```

**Key points:**

- `capture` extracts a field from the current step's result and stores it in `Vars` under the given key.
- **JSONPath** is used for JSON responses (HTTP bodies): `"$.id"`, `"$.items[0].sku"`, etc.
- **XPath** is used for XML responses: `"/root/element/text()"`.
- A `{placeholder}` in a later step is replaced with the captured value at execution time.
- Captures are available to **all subsequent steps** in the same scenario.
- If a capture expression does not match the result (e.g., JSONPath `$.missing_field` on a response that has no such field), the step fails with a clear error unless you add error handling (see the Language Reference for capture-failure behaviour).

---

## Engine-owned polling with verifyMode: RETRY

**When to use:** A condition is not immediately true but is expected to become true within a bounded time window (e.g., an async job, an outbound webhook, a replicated database). You want the engine to poll automatically rather than manually writing `Thread.Sleep` loops.

**How it works:** A step with `verifyMode: RETRY` uses **engine-owned polling** (powered by Polly v8) with exponential backoff. The engine repeatedly executes the step until its assertions pass or a timeout expires. This is a provider-agnostic feature — it works on any step type and surfaces per-attempt timelines in the HTML report.

### Example: polling a webhook listener

```yaml
metadata:
  name: async-webhook-polling
  owner: team-webhooks
  tags: [integration, async]
  description: >-
    Trigger an async job, poll a webhook listener until the expected inbound
    request arrives, then verify the payload.

environment:
  services:
    job-processor:
      image: myco/job-processor:latest
      httpPort: 8080

steps:
  # Step 1: Trigger an async job (fire-and-forget)
  - id: trigger-job
    type: http.rest
    target: job-processor
    method: POST
    path: /jobs
    body:
      job_type: "email-send"
      recipient: "alice@example.com"
      template: "order-confirmation"
    expect:
      status: 202
    capture:
      job_id: "$.jobId"

  # Step 2: Stand up a webhook listener and wait for the job to call it back
  # (The engine stands up a Kestrel listener on an unguessable path)
  - id: receive-webhook
    type: webhook-listen.http
    listener: job-webhook
    timeout: 30s
    match:
      method: POST
      path: "/webhook/.*"
      bodyContains: "alice@example.com"
    capture:
      webhook_payload: "$"

  # Step 3: Async assertion — keep checking the database until the job has
  # completed and recorded its result. Use RETRY to poll automatically.
  - id: wait-for-job-completion
    type: db-assert.postgres
    target: jobs-db
    verifyMode: RETRY
    timeout: 60s
    query: >-
      SELECT status, completed_at
      FROM jobs
      WHERE id = @job_id
    parameters:
      job_id: "{job_id}"
    expect:
      rowCount: 1
      row:
        status: "completed"
    capture:
      completed_at: "$.completed_at"
```

**Key points:**

- `verifyMode: RETRY` enables engine-owned polling. The step is re-executed repeatedly (with exponential backoff) until it passes or `timeout` expires.
- `timeout` bounds the entire polling window (e.g., `60s` means the engine stops retrying after 60 seconds).
- If the timeout expires before the assertion passes, the step fails with an `Inconclusive` verdict (the engine could not decide, not a product defect).
- **Each attempt is recorded individually** in the `--events` output and rendered with a timeline in the HTML report, so you can see exactly when assertions started passing.
- RETRY is available on **any step type** (not just database assertions).
- **Authors never write `Thread.Sleep`** — the engine owns the backoff strategy.
- For polling parameters (backoff curve, max attempts, etc.), see `docs/02` §7 and `docs/01` §5.7 (Polly v8 resilience pipelines). The per-attempt timeline is rendered in the HTML report.

---

## CI integration with GitHub Actions

**Quick start:** The vouchfx repository ships a reusable GitHub Actions workflow that runs a vouchfx suite and publishes HTML and JUnit reports. Any repository can call it.

**Minimal example:**

In your repository's `.github/workflows/e2e.yml`:

```yaml
name: E2E Tests

on: [push, pull_request]

jobs:
  vouchfx-e2e:
    uses: vouchfx-org/vouchfx/.github/workflows/vouchfx-run.yml@<40-char-commit-sha>
    with:
      scenario-path: ./tests/e2e
      fail-on-env-error: false
```

Replace `<40-char-commit-sha>` with a full commit SHA (not a branch or tag) for supply-chain repeatability.

**Configuration options:**

| Input | Type | Default | Purpose |
|---|---|---|---|
| `scenario-path` | string | `.` | Directory (relative to checkout) to search recursively for `.e2e.yaml` files. |
| `fail-on-env-error` | boolean | `false` | When `true`, environment errors (unhealthy container, image-pull failure, seed failure) fail the job with exit code 3. |
| `fail-on-inconclusive` | boolean | `false` | When `true`, inconclusive verdicts (timeout, unmet captures) fail the job with exit code 4. |
| `prewarm-images` | string | (empty) | Optional newline-separated list of container images to `docker pull` before the run, to warm the Docker cache and mitigate cold-start delays. |

**Reports:**

The workflow always publishes artifacts (even on failure, via `if: always()`):

- **`results.xml`** — JUnit XML for CI ingestion. The four verdicts map to distinct primitives: Fail → `<failure>`, EnvironmentError → `<error>`, Inconclusive → `<skipped>`, Pass → success.
- **`report.html`** — Self-contained HTML report with polling timelines, captured-variable provenance, failed-step diffs, and the reproducibility envelope (no secret values embedded).

**Exit codes:**

| Code | Meaning | Default? |
|---|---|---|
| 0 | Success (Pass, or EnvironmentError/Inconclusive by default) | Yes |
| 1 | Fail (a genuine product defect) | Always breaks CI |
| 3 | EnvironmentError (infrastructure breakage) | Only if `fail-on-env-error: true` |
| 4 | Inconclusive (timeout, unmet captures) | Only if `fail-on-inconclusive: true` |

For the full reference, see [`README.md` § CI integration with GitHub Actions](../README.md#ci-integration-with-github-actions) and [`.github/workflows/vouchfx-run.yml`](../.github/workflows/vouchfx-run.yml).

---

## CI integration with GitLab CI

**Quick start:** The vouchfx repository ships a GitLab CI/CD template that runs a vouchfx suite and publishes JUnit and HTML reports. Any project can include it.

**Minimal example:**

In your project's `.gitlab-ci.yml`:

```yaml
include:
  - project: vouchfx-org/vouchfx
    ref: <40-char-commit-sha>
    file: /ci/gitlab/vouchfx-run.gitlab-ci.yml

vouchfx-run:
  variables:
    VOUCHFX_SCENARIO_PATH: ./tests/e2e
    VOUCHFX_FAIL_ON_ENV_ERROR: "false"
```

Replace `<40-char-commit-sha>` with a full commit SHA for supply-chain repeatability.

**Configuration variables:**

| Variable | Type | Default | Purpose |
|---|---|---|---|
| `VOUCHFX_SCENARIO_PATH` | string | `.` | Directory (relative to project root) to search recursively for `.e2e.yaml` files. |
| `VOUCHFX_FAIL_ON_ENV_ERROR` | string | `"false"` | When truthy, environment errors fail the job with exit code 3. |
| `VOUCHFX_FAIL_ON_INCONCLUSIVE` | string | `"false"` | When truthy, inconclusive verdicts fail the job with exit code 4. |
| `VOUCHFX_PREWARM_IMAGES` | string | (empty) | Optional whitespace/newline-separated list of container images to pre-warm. |

**Docker-in-Docker requirement:**

The template uses GitLab's `docker:dind` service, which requires a **privileged runner** (Docker executor with `privileged = true`, or equivalent on Kubernetes). On gitlab.com's shared runners, this is available by default. Self-managed runners must be explicitly configured for it.

**Reports:**

Reports are published to the job's default artifact path (native GitLab test-report rendering):

- **`results.xml`** — JUnit XML, surfaced in GitLab's pipeline and merge-request test-report UI.
- **`report.html`** — Self-contained HTML report.

**Verification status:** The GitLab template is static-validated (schema, behavioural equivalence) but has not been run on a live GitLab instance — a live pipeline run is a follow-up. The primary unknown is whether vouchfx's Aspire/DCP-managed containers are reachable under dind (set `TESTCONTAINERS_HOST_OVERRIDE=docker` in the template).

For the full reference, see [`README.md` § CI integration with GitLab CI](../README.md#ci-integration-with-gitlab-ci) and [`ci/gitlab/vouchfx-run.gitlab-ci.yml`](../ci/gitlab/vouchfx-run.gitlab-ci.yml).

---

## See also

- **[Getting Started](getting-started.md)** — Your first vouchfx test in 60 minutes.
- **[Language Reference](language-reference.md)** — Complete per-step-type field reference (required/optional, types, descriptions). Auto-generated from the schema.
- **[Common Patterns](common-patterns.md)** — Authoring patterns: the file structure, state threading, filtering, and scenario selection.
- **[Troubleshooting](troubleshooting.md)** — Real failure modes and how to fix them.
- **[Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md)** — How the system works (layers, memory model, orchestration, security, provider architecture).
- **[YAML DSL Specification](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)** — The full `.e2e.yaml` grammar and JSON Schema.
