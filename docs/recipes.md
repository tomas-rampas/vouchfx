# vouchfx Recipes: Common Patterns and Examples

This document collects task-oriented recipes for common testing scenarios in vouchfx. Each recipe is a self-contained, runnable `.e2e.yaml` file (or pattern) with explanation.

**Table of contents:**
1. [Seeding with SQL fixtures](#seeding-with-sql-fixtures)
2. [Test doubles with WireMock](#test-doubles-with-wiremock)
3. [Injecting secrets from environment variables](#injecting-secrets-from-environment-variables)
4. [Injecting secrets from Vault](#injecting-secrets-from-vault)
5. [Multi-step state threading with capture and placeholders](#multi-step-state-threading-with-capture-and-placeholders)
6. [Engine-owned polling with verifyMode: RETRY](#engine-owned-polling-with-verifymode-retry)
7. [Publish and consume a Kafka event](#publish-and-consume-a-kafka-event)
8. [Verify a RabbitMQ queue receives an event](#verify-a-rabbitmq-queue-receives-an-event)
9. [Verify a NATS JetStream event](#verify-a-nats-jetstream-event)
10. [Publish and expect through Azure Service Bus](#publish-and-expect-through-azure-service-bus)
11. [Assert a Redis cache entry](#assert-a-redis-cache-entry)
12. [Assert an Elasticsearch document](#assert-an-elasticsearch-document)
13. [Capture an outbound email with Mailpit](#capture-an-outbound-email-with-mailpit)
14. [CI integration with GitHub Actions](#ci-integration-with-github-actions)
15. [CI integration with GitLab CI](#ci-integration-with-gitlab-ci)

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
vouchfx run tests/integrations
```

**Key points:**

- **Secret references are resolved at execution time**, not compile time — the resolved value never enters source code, logs, or reports.
- The `${secret:env/VAR_NAME}` reference appears in output **only with the value redacted**: `${secret:env/EXTERNAL_API_KEY} (redacted)`.
- The **resolved value never appears anywhere** — not in terminal output, not in `--events` JSON Lines, not in the HTML report.
- Any verbatim occurrence of a resolved secret value in a step's observation text (e.g. a thrown exception message in a `script.csharp` step) is automatically redacted before reaching the terminal output, the `--events` stream, or any report; however, authors should still avoid embedding secrets in exception messages because the scrub cannot catch deliberately transformed values (base64, HMAC, substrings).
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

## Publish and consume a Kafka event

**When to use:** You need to verify that your system publishes and consumes messages correctly on a Kafka topic, with schema and retry semantics.

**How it works:** The `mq-publish.kafka` step sends a message to a Kafka topic, optionally with headers and a key. The `mq-expect.kafka` step consumes messages and asserts they match declared criteria (key, headers, payload substring, JSONPath over the payload). Using `verifyMode: RETRY` on the consume step makes the engine poll automatically until the expected message appears or the timeout expires.

### Example: order event publish and verify

```yaml
metadata:
  name: kafka-order-event-flow
  owner: team-events
  tags: [integration, kafka, messaging]
  description: Publish an order event to Kafka and verify the expected message arrives with correct schema.

environment:
  services:
    order-service:
      image: myco/order-service:latest
      httpPort: 8080
  dependencies:
    orders-kafka:
      type: kafka

steps:
  # Step 1: Trigger the service to publish an order event
  - id: create-order
    type: http.rest
    target: order-service
    method: POST
    path: /orders
    body:
      customerId: "cust-001"
      amount: 199.99
      items: [ { sku: "WIDGET-1", qty: 2 } ]
    expect:
      status: 201
    capture:
      orderId: "$.id"
      createdAt: "$.createdAt"

  # Step 2: Directly publish a test event (optional seed step)
  - id: publish-order-event
    type: mq-publish.kafka
    description: Directly publish an order event with headers and a key.
    target: orders-kafka
    topic: orders.created
    key: "cust-001"
    payload: '{"eventId":"evt-123","orderId":"ord-456","customerId":"cust-001","amount":199.99,"createdAt":"2026-07-04T10:30:00Z"}'
    headers:
      x-event-version: "1.0"
      x-source: test-suite

  # Step 3: Assert the message was published (with polling)
  - id: verify-order-published
    type: mq-expect.kafka
    description: >
      Verify that an order event with the expected properties arrived on the topic.
      Polling ensures eventual consistency — the message may not be immediately available.
    target: orders-kafka
    topic: orders.created
    match:
      payloadContains: "customerId"
      json:
        $.orderId: ord-456
        $.amount: "199.99"
      headers:
        x-event-version: "1.0"
    verifyMode: RETRY
    timeout: 30s
    capture:
      eventPayload: "$"
```

**Key points:**

- **Topic:** The Kafka topic name. May contain `{placeholder}` and `${secret:source/path}` tokens.
- **Key:** Optional. Used for partitioning and ordering in Kafka. If multiple messages are published to the same key within the polling window, all are checked; the assertion passes on the first match.
- **Payload:** The message value (UTF-8 string or inline JSON). May contain placeholders and secrets.
- **Headers:** Optional map of header names to values. All declared headers must match for the assertion to pass (AND-conjunctive).
- **Match criteria:** AND-conjunctive — a message must satisfy all declared criteria (key, all headers, payload substring, and all JSONPath expressions) to match.
- **Avro codec (advanced):** The `avro` field (with `schemaRegistry`, `subject`, `schema`, and `record` sub-fields) allows publishing and consuming Avro-encoded messages via Confluent Schema Registry.
- **Polling:** `verifyMode: RETRY` polls the Kafka broker until a matching message is found or the timeout expires. Each poll is independent, so the message is re-evaluated on every attempt.

**For a complete runnable example,** see the Kafka publish/expect steps in [`examples/reference/reference.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/reference/reference.e2e.yaml) and the orders-dotnet sample in the [vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/orders-dotnet) repository.

---

## Verify a RabbitMQ queue receives an event

**When to use:** Your system publishes to a RabbitMQ queue, and you want to assert that the expected message arrived and has the right properties.

**How it works:** RabbitMQ uses AMQP 0-9-1 broker semantics. Queues must exist before messages can be routed to them — either declared by your system-under-test at startup, or declared in the test's `environment.dependencies`. The `mq-expect.rabbitmq` step non-destructively reads from the queue and asserts on message body, headers, and payload structure.

### Example: order queue verification

```yaml
metadata:
  name: rabbitmq-order-queue
  owner: team-events
  tags: [integration, rabbitmq, messaging]
  description: Verify that a system-under-test publishes order notifications to the correct RabbitMQ queue.

environment:
  services:
    notification-service:
      image: myco/notification-service:latest
      httpPort: 8080
  dependencies:
    events-rmq:
      type: rabbitmq

steps:
  - id: trigger-notification
    type: http.rest
    target: notification-service
    method: POST
    path: /orders/{orderId}/notify
    body:
      orderId: "ord-789"
      recipient: "alice@example.com"
      template: "order-confirmed"
    expect:
      status: 202

  - id: assert-queue-message
    type: mq-expect.rabbitmq
    description: >
      Verify the message arrived in the orders-notifications queue.
      The queue must already exist (either created by the SUT or declared in environment).
    target: events-rmq
    queue: orders-notifications
    match:
      payloadContains: "order-confirmed"
      headers:
        x-event-type: notification
      json:
        $.orderId: ord-789
    verifyMode: RETRY
    timeout: 15s
```

**Key points:**

- **Queue ownership:** The queue (`orders-notifications` in the example) must exist before the test runs. If your SUT declares it at startup, the first HTTP step will trigger that declaration before the queue assertion runs. Alternatively, seed it explicitly in the `environment` config or via a `script.csharp` step.
- **Routing key:** When publishing, messages are routed via the routing key (often matching or derived from the queue name in simple topologies). In `mq-expect`, the `queue` field specifies which queue to read from.
- **Durability:** RabbitMQ queues can be transient or durable (persisting across broker restarts). Declare test queues **durable** unless they are exclusive: RabbitMQ 4.x deprecates transient non-exclusive queues and refuses the declaration outright (AMQP 541 `INTERNAL_ERROR`, `transient_nonexcl_queues`). Durability costs nothing in a throwaway test container.
- **Message consumption:** The `mq-expect.rabbitmq` step peeks the queue without consuming (deleting) the message, so multiple assertions on the same queue in the same scenario will all see the same messages.

**For a complete runnable example,** see [`examples/mq-rabbitmq.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/mq-rabbitmq.e2e.yaml) and the inventory-python sample in [vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/inventory-python). (The linked file is the standalone runnable variant: its comments explain how the in-suite write or publish stands in for the real-SUT behaviour shown in this recipe.)

---

## Verify a NATS JetStream event

**When to use:** You are using NATS JetStream (the durable, stream-based pub/sub model) and need to assert that expected messages have been retained and can be consumed.

**How it works:** NATS JetStream automatically creates streams that match declared subjects (e.g., a subject `orders.created` creates a stream named `ORDERS_CREATED`). Messages are durably retained in the stream, so the mq-expect step can scan from the beginning of the log on every retry attempt, making assertions idempotent.

**Important caution:** Do not share a single NATS dependency across multiple scenarios that assert on the same subject, because retained messages from previous scenario runs will be seen by subsequent scenarios, causing false passes. Use either separate NATS containers per scenario or explicit `stream` names to isolate them.

### Example: order event assertion with JetStream

```yaml
metadata:
  name: nats-jetstream-order-verification
  owner: team-events
  tags: [integration, nats, messaging]
  description: >
    Verify that an order event was published to a NATS JetStream subject.
    The provider automatically creates the stream if it does not exist.

environment:
  services:
    order-api:
      image: myco/order-api:latest
      httpPort: 8080
  dependencies:
    # Each scenario gets its own nats container to avoid cross-scenario state bleed
    nats-broker:
      type: nats

steps:
  - id: create-order
    type: http.rest
    target: order-api
    method: POST
    path: /orders
    body:
      customerId: "cust-001"
      amount: 149.99
    expect:
      status: 201
    capture:
      orderId: "$.id"

  - id: publish-jetstream-event
    type: mq-publish.nats
    description: >
      Publish an order event to NATS JetStream. The provider creates the stream
      covering the 'orders.created' subject if it does not exist.
    target: nats-broker
    subject: orders.created
    payload: '{"eventType":"order-created","orderId":"{orderId}","customerId":"cust-001","amount":149.99}'

  - id: assert-order-in-stream
    type: mq-expect.nats
    description: >
      Assert that a message matching the criteria is present on the stream.
      The consumer scans from the beginning of the retained log on every RETRY
      attempt, so the assertion is idempotent.
    target: nats-broker
    subject: orders.created
    match:
      payloadContains: "order-created"
      json:
        $.orderId: "{orderId}"
    verifyMode: RETRY
    timeout: 30s
```

**Key points:**

- **Stream creation:** The provider automatically creates a JetStream stream whose name is derived from the subject by uppercasing and replacing non-alphanumeric characters with underscores (e.g., `orders.created` → `ORDERS_CREATED`). Override this with the optional `stream` field if you need an explicit name.
- **Subject filtering:** JetStream subjects use a dot-notation hierarchy (e.g., `orders.created`, `orders.shipped`). A single stream can cover multiple subjects with wildcards, but for simplicity, declare one subject per step.
- **Idempotent consumption:** The consumer scans from the start of the retained log on every attempt, so messages are re-evaluated without side effects — perfect for polling with RETRY.
- **Message ordering:** JetStream guarantees FIFO ordering within a subject. If you publish multiple messages to the same subject, the consumer will see them in order.
- **Cross-scenario isolation:** Because the retained log persists across assertions within a scenario, **do not reuse a single NATS dependency across multiple sequential scenarios on the same subject.** Use separate `nats` dependency declarations per scenario, or assign unique `stream` names if you must reuse the dependency.

**For a complete runnable example,** see [`examples/mq-nats.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/mq-nats.e2e.yaml) and the payments-java sample in [vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/payments-java). (The linked file is the standalone runnable variant: its comments explain how the in-suite write or publish stands in for the real-SUT behaviour shown in this recipe.)

---

## Publish and expect through Azure Service Bus

**When to use:** Your system uses Azure Service Bus (or the emulator) for asynchronous messaging, and you want to verify that messages arrive in queues or topic subscriptions.

**How it works:** The Azure Service Bus Emulator (or a live Azure subscription) must be configured with queue and topic declarations in the test's `environment.dependencies`. The emulator runs as a container with a SQL Server sidecar for persistence. The `mq-publish.azureservicebus` step sends messages to a queue or topic; the `mq-expect.azureservicebus` step uses non-destructive peek to assert messages are present without consuming them.

### Example: order message with topic subscription

```yaml
metadata:
  name: azure-servicebus-order-events
  owner: team-events
  tags: [integration, azure-servicebus, messaging]
  description: >
    Publish an order event to an Azure Service Bus topic and verify
    it arrives at a topic subscription using the emulator.

environment:
  services:
    order-api:
      image: myco/order-api:latest
      httpPort: 8080

  dependencies:
    # Azure Service Bus Emulator: requires entity declarations
    # Entities NOT declared here will not exist in the emulator
    service-bus:
      type: azureservicebus
      queues:
        - order-commands
      topics:
        - name: order-events
          subscriptions:
            - fulfillment-sub
            - billing-sub

steps:
  - id: create-order
    type: http.rest
    target: order-api
    method: POST
    path: /orders
    body:
      customerId: "cust-123"
      amount: 299.99
      items: [ { sku: "PREMIUM", qty: 1 } ]
    expect:
      status: 201
    capture:
      orderId: "$.id"

  - id: publish-to-queue
    type: mq-publish.azureservicebus
    description: Publish an order command to a queue.
    target: service-bus
    queue: order-commands
    payload: '{"orderId":"{orderId}","action":"fulfill","customerId":"cust-123"}'
    properties:
      correlationId: "corr-12345"
      contentType: "application/json"

  - id: publish-to-topic
    type: mq-publish.azureservicebus
    description: Publish an order event to a topic for multiple subscribers.
    target: service-bus
    topic: order-events
    payload: '{"orderId":"{orderId}","eventType":"order-created","customerId":"cust-123","amount":299.99}'
    properties:
      eventType: order-created

  - id: assert-command-in-queue
    type: mq-expect.azureservicebus
    description: Verify the order command arrived in the queue.
    target: service-bus
    queue: order-commands
    expectPayloadContains: "fulfill"
    expectProperties:
      correlationId: "corr-12345"
    verifyMode: RETRY
    timeout: 30s

  - id: assert-event-in-subscription
    type: mq-expect.azureservicebus
    description: Verify the event arrived at the fulfillment subscription.
    target: service-bus
    topic: order-events
    subscription: fulfillment-sub
    expectPayloadContains: "order-created"
    expectProperties:
      eventType: order-created
    verifyMode: RETRY
    timeout: 30s
```

**Key points:**

- **Entity declaration:** Queues and topics (including their subscriptions) must be declared under `environment.dependencies.service-bus` for the emulator to create them. Messages sent to undeclared entities fail with `EnvironmentError`.
- **Queue vs. topic:** Use `queue` for point-to-point messaging; use `topic` + `subscription` for pub/sub. Multiple subscriptions on the same topic each receive a copy of published messages.
- **Peeking, not consuming:** The `mq-expect` step uses `PeekMessagesAsync`, which is non-destructive — messages remain in the queue/subscription for subsequent assertions or real consumers.
- **Properties:** Custom application-level properties (key-value pairs) are set via the `properties` map on the publish step and matched via `expectProperties` on the assert step.
- **Emulator setup:** The emulator requires Docker and automatically starts a SQL Server container for persistence. If using a live Azure Service Bus, provide a valid connection string via the `environment` or `${secret:}` injection.

**For a complete runnable example,** see [`examples/mq-azureservicebus.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/mq-azureservicebus.e2e.yaml). (The linked file is the standalone runnable variant: its comments explain how the in-suite write or publish stands in for the real-SUT behaviour shown in this recipe.)

---

## Assert a Redis cache entry

**When to use:** Your system writes data to Redis (strings, hashes, lists, sets), and you want to verify the cached state without needing to query through your application.

**How it works:** The `cache-assert.redis` step opens a connection to Redis, runs a single operation (GET, EXISTS, TTL, HGET, HLEN, LLEN, SCARD), and asserts on the returned value, key presence, or collection size. Multiple assertion operations can verify different shapes of cached data.

### Example: session cache verification

```yaml
metadata:
  name: redis-session-cache
  owner: team-infra
  tags: [integration, redis, caching]
  description: Verify that session data is cached in Redis with correct structure and TTL.

environment:
  services:
    session-api:
      image: myco/session-api:latest
      httpPort: 8080
  dependencies:
    cache:
      type: redis

steps:
  - id: create-session
    type: http.rest
    target: session-api
    method: POST
    path: /sessions
    body:
      userId: "user-42"
      ttl: 3600
    expect:
      status: 201
    capture:
      sessionId: "$.sessionId"
      userId: "$.userId"

  - id: assert-session-string
    type: cache-assert.redis
    description: Verify the session value is cached (GET operation).
    target: cache
    key: "session:{sessionId}"
    operation: get
    expect:
      value: "active"
    verifyMode: RETRY
    timeout: 10s

  - id: assert-session-exists
    type: cache-assert.redis
    description: Verify the session key exists.
    target: cache
    key: "session:{sessionId}"
    operation: exists
    expect:
      exists: true

  - id: assert-session-has-ttl
    type: cache-assert.redis
    description: >
      Verify the session key has a positive TTL (expiry time set).
      Note: exact TTL values are time-sensitive and not asserted; only presence.
    target: cache
    key: "session:{sessionId}"
    operation: ttl
    expect:
      exists: true  # true = has a positive TTL; false = no expiry or key not found

  - id: assert-profile-hash
    type: cache-assert.redis
    description: Verify a specific field in a hash (HGET operation).
    target: cache
    key: "user:{userId}:profile"
    operation: hget
    field: status
    expect:
      value: verified

  - id: assert-queue-length
    type: cache-assert.redis
    description: Verify the list has exactly 3 items (LLEN operation).
    target: cache
    key: "user:{userId}:queue"
    operation: llen
    expect:
      length: 3
```

**Key points:**

- **Operations:** `get` (string value), `exists` (key presence), `ttl` (has positive TTL), `hget` (hash field), `hlen` (hash size), `llen` (list length), `scard` (set cardinality).
- **TTL semantics:** The `ttl` operation asserts only whether the key has a positive time-to-live (expiry set), not the exact remaining TTL. Exact TTL checks are too time-sensitive for CI and would flake.
- **Field names:** For `hget`, the `field` parameter specifies the hash field to retrieve.
- **Placeholders:** The `key`, `field`, and expected `value` may all contain `{placeholder}` tokens that are substituted at execution time.
- **Exact-match vs. exists:** For string values (GET), `expect.value` is an ordinal equality check. For presence checks (EXISTS), use `expect.exists: true/false`.

**For a complete runnable example,** see [`examples/cache-assert-redis.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/cache-assert-redis.e2e.yaml). (The linked file is the standalone runnable variant: its comments explain how the in-suite write or publish stands in for the real-SUT behaviour shown in this recipe.)

---

## Assert an Elasticsearch document

**When to use:** Your system indexes data into Elasticsearch, and you want to verify that documents matching certain criteria are present in an index with expected field values.

**How it works:** The `cache-assert.elasticsearch` step POSTs an Elasticsearch Query DSL body to the declared index, parses the hit count and optional first-hit `_source` fields, and asserts on their values. This is particularly useful for testing search and indexing pipelines.

### Example: order document indexing

```yaml
metadata:
  name: elasticsearch-order-index
  owner: team-search
  tags: [integration, elasticsearch, indexing]
  description: Verify that order data is correctly indexed in Elasticsearch.

environment:
  services:
    order-api:
      image: myco/order-api:latest
      httpPort: 8080
  dependencies:
    search:
      type: elasticsearch

steps:
  - id: create-order
    type: http.rest
    target: order-api
    method: POST
    path: /orders
    body:
      orderId: "ord-999"
      customerId: "cust-456"
      status: "confirmed"
      region: "EMEA"
      amount: 599.99
    expect:
      status: 201
    capture:
      orderId: "$.orderId"

  - id: check-indexed
    type: cache-assert.elasticsearch
    description: >
      Query the orders index for the document and verify it exists with correct fields.
      The query uses Elasticsearch Query DSL (term query for exact match).
    target: search
    index: orders
    query: '{"query":{"term":{"orderId":"{orderId}"}}}'
    expect:
      count: 1
      fields:
        - field: status
          value: confirmed
        - field: region
          value: EMEA
    verifyMode: RETRY
    timeout: 30s
```

**Key points:**

- **Query DSL:** The `query` field is an inline Elasticsearch Query DSL JSON body (not a simple query string). Common queries include `term`, `match`, `bool`, `range`, etc. The query is POSTed to `{url}/{index}/_search`.
- **Field extraction:** The `expect.fields` list declares which `_source` fields of the first hit to assert on. Only top-level (flat) `_source` field names are supported; dot-notation for nested objects is rejected at validation time.
- **Count assertion:** The `expect.count` field asserts on the total hit count. If the count does not match, the step fails; if it matches but field values do not, the step also fails.
- **Placeholders:** The `query` body may contain `{placeholder}` tokens, which are substituted into the JSON structure at execution time.
- **Idempotent polling:** `verifyMode: RETRY` is ideal for indexing assertions, because newly indexed documents may take a moment to become searchable.

**For a complete runnable example,** see [`examples/cache-assert-elasticsearch.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/cache-assert-elasticsearch.e2e.yaml). (The linked file is the standalone runnable variant: its comments explain how the in-suite write or publish stands in for the real-SUT behaviour shown in this recipe.)

---

## Capture an outbound email with Mailpit

**When to use:** Your system sends email via SMTP, and you want to verify that the correct emails are sent to the right recipients with expected content.

**How it works:** Mailpit is a simple SMTP server with a REST API. Your system-under-test connects to Mailpit's SMTP port (1025) and delivers email normally. The `mail-expect.smtp` step queries Mailpit's HTTP API to enumerate and filter messages by recipient (To), subject substring, and body substring, then asserts on the count of matches.

### Example: welcome email assertion

```yaml
metadata:
  name: mailpit-welcome-email
  owner: team-notifications
  tags: [integration, email, smtp]
  description: >
    Trigger a welcome email and verify it arrives at the expected recipient
    with correct subject and body content via Mailpit.

environment:
  services:
    app:
      image: myco/app:latest
      httpPort: 8080
      env:
        # Inject the SMTP endpoint so the SUT knows where to send mail
        SMTP_HOST: ${conn:mail.host}
        SMTP_PORT: ${conn:mail.port}
  dependencies:
    mail:
      type: mailpit

steps:
  - id: trigger-welcome-email
    type: http.rest
    description: Trigger the app to send a welcome email.
    target: app
    method: POST
    path: /users
    body:
      name: "Alice"
      email: "alice@example.com"
    expect:
      status: 201
    capture:
      userId: "$.id"

  - id: assert-welcome-email
    type: mail-expect.smtp
    description: >
      Assert that exactly one welcome email was sent to alice@example.com
      with the expected subject and body content.
      verifyMode: RETRY polls Mailpit's API until the email arrives.
    target: mail
    expect:
      count: 1
      match:
        to: alice@example.com
        subject-contains: "Welcome"
        body-contains: "Hello, Alice"
    verifyMode: RETRY
    timeout: 30s
```

**Key points:**

- **Mailpit configuration:** The dependency type is `mailpit`. The engine stages two endpoints:
  - `conn::mail` — HTTP API URL for querying messages (e.g., `http://localhost:8025`).
  - `svc::mail-smtp` — SMTP address and port for your SUT to deliver mail (host and port separately via `${conn:mail.host}` and `${conn:mail.port}`).
- **Match criteria (AND-conjunctive):** All declared criteria must be satisfied:
  - `to` — the recipient address, compared case-insensitively against each address in the message's To list.
  - `subject-contains` — the subject line must contain this substring (case-sensitive).
  - `body-contains` — the email body (plain-text part) must contain this substring (case-sensitive).
- **Count assertion:** The `expect.count` field asserts on the number of messages matching all criteria. If the count does not match, the step fails.
- **Polling:** `verifyMode: RETRY` is essential for email testing, because SMTP delivery is asynchronous.
- **SUT configuration:** Ensure your system-under-test reads the SMTP host and port from the environment (e.g., `${conn:mail.host}` and `${conn:mail.port}` injected as env vars). Without this, the SUT cannot connect to Mailpit.

**For a complete runnable example,** see [`examples/mail-expect-smtp.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/mail-expect-smtp.e2e.yaml). (The linked file is the standalone runnable variant: its comments explain how the in-suite write or publish stands in for the real-SUT behaviour shown in this recipe.)

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
    uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@<40-char-commit-sha>
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
| 4 | Inconclusive (timeout, unmet captures; or every scenario failed to parse) | Only if `fail-on-inconclusive: true` — but an all-parse-failure run always exits 4 |

For the full reference, see [CI integration reference § GitHub Actions](ci-integration.md#github-actions) and [`.github/workflows/vouchfx-run.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/.github/workflows/vouchfx-run.yml).

---

## CI integration with GitLab CI

**Quick start:** The vouchfx repository ships a GitLab CI/CD template that runs a vouchfx suite and publishes JUnit and HTML reports. Any project can include it.

**Minimal example:**

In your project's `.gitlab-ci.yml`:

```yaml
include:
  - project: tomas-rampas/vouchfx
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

For the full reference, see [CI integration reference § GitLab CI](ci-integration.md#gitlab-ci) and [`ci/gitlab/vouchfx-run.gitlab-ci.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/vouchfx-run.gitlab-ci.yml).

---

## See also

- **[Getting Started](getting-started.md)** — Your first vouchfx test in 60 minutes.
- **[vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples)** — Real-world sample applications and complete test suites demonstrating patterns across multiple providers and technologies.
- **[Language Reference](language-reference.md)** — Complete per-step-type field reference (required/optional, types, descriptions). Auto-generated from the schema.
- **[Common Patterns](common-patterns.md)** — Authoring patterns: the file structure, state threading, filtering, and scenario selection.
- **[Troubleshooting](troubleshooting.md)** — Real failure modes and how to fix them.
- **[Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md)** — How the system works (layers, memory model, orchestration, security, provider architecture).
- **[YAML DSL Specification](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)** — The full `.e2e.yaml` grammar and JSON Schema.
