# vouchfx Common Patterns

This guide covers the structural and compositional patterns that appear in most vouchfx test files. It complements the [Language Reference](language-reference.md) (which documents every field) and the [Recipes](recipes.md) (which shows task-oriented examples).

**Table of contents:**
1. [The four top-level sections](#the-four-top-level-sections)
2. [Metadata and test selection](#metadata-and-test-selection)
3. [Environment: services and dependencies](#environment-services-and-dependencies)
4. [Configuring the system under test from the environment block](#configuring-the-system-under-test-from-the-environment-block)
5. [Capturing and threading state forward](#capturing-and-threading-state-forward)
6. [The four verdicts and `continueOnFailure`](#the-four-verdicts-and-continueonfailure)
7. [Common step patterns](#common-step-patterns)
8. [Multi-step workflows: the reference scenario](#multi-step-workflows-the-reference-scenario)

---

## The four top-level sections

Every `.e2e.yaml` file has this shape (only `steps` is required):

```yaml
metadata:
  name: ...
  owner: ...
  tags: [...]
  description: ...

environment:
  services:
    service_name:
      image: ...
  dependencies:
    db_name:
      type: ...
  seed:
    db_name:
      sql: [...]
  imageRegistry: ...
  imagePullPolicy: ...

variables:
  var_name: value
  another_var: value

steps:
  - id: step-id
    type: family.provider
    # step-specific fields...
```

### metadata

The **metadata** section is optional but recommended. It contains:

- **`name`** (string) — A human-readable test name, shown in reports. Use lowercase-with-hyphens.
- **`owner`** (string) — Team or person responsible. Used for selecting tests (e.g., `vouchfx run --owner team-a`).
- **`tags`** (array of strings) — Labels for filtering (e.g., `[smoke, integration, high-priority]`). Use `vouchfx run --tag smoke --tag integration` to select tests by tag.
- **`description`** (string, multi-line OK) — Human-facing explanation shown in test output. Use `>-` for wrapped text.

**Example:**

```yaml
metadata:
  name: payment-processing-smoke
  owner: team-payments
  tags: [smoke, critical]
  description: >-
    Verify a payment is accepted, recorded in the database,
    and triggers a confirmation email.
```

Metadata drives test **selection and reporting**, not execution. See [Test Runner Selection](#metadata-and-test-selection) below.

### environment

The **environment** section declares the infrastructure your test depends on.

#### services

Named services under test (REST APIs, gRPC servers, message brokers you call). Each service is a container:

```yaml
environment:
  services:
    order-api:
      image: myco/order-service:latest
      httpPort: 8080
      env:
        DATABASE_URL: "postgres://..."
        LOG_LEVEL: "debug"
    payment-api:
      image: myco/payment-service:v2.1.0
      httpPort: 9000
      grpcPort: 9001
```

Each service must declare at least one port (`httpPort`, `grpcPort`, etc.) so vouchfx can health-gate it before running steps.

#### dependencies

Named managed resources (databases, message brokers, caches) that vouchfx orchestrates via **Aspire**:

```yaml
environment:
  dependencies:
    orders-db:
      type: postgres
    kafka:
      type: kafka
    cache:
      type: redis
```

Vouchfx stands up each dependency, waits for it to be healthy, applies any `seed`, and then makes it available to your steps. Each dependency has a default image and tag pinned by the Aspire module (e.g., Aspire pins PostgreSQL 18.3). Authors can override this with a per-dependency `version:` field (to change only the tag) or `image:` field (to replace the entire image reference), and can redirect wholesale with `imageRegistry` at the environment level. A dependency may also carry an `env:` map to configure its container — see [Configuring a managed dependency with environment variables](#configuring-a-managed-dependency-with-environment-variables) below. Steps reference dependencies by their logical name (e.g., `target: orders-db`).

#### seed

Optional SQL fixtures applied to each dependency **after it is healthy but before step 1 runs**:

```yaml
environment:
  dependencies:
    mydb:
      type: postgres
  seed:
    mydb:
      sql: [ "fixtures/init.sql", "fixtures/ref-data.sql" ]
```

Files are paths **relative to the scenario directory**. Seed failures produce an `EnvironmentError` verdict (infrastructure problem), not a test failure.

#### imageRegistry and imagePullPolicy

Optional overrides for Docker image resolution:

```yaml
environment:
  imageRegistry: myregistry.azurecr.io
  services:
    myservice:
      image: myimage:latest  # Resolved as myregistry.azurecr.io/myimage:latest
```

> **Note:** Set `imagePullPolicy` at the **environment level** (applies to every container, including managed dependencies) or per-**service** to override it there; there is **no per-dependency form**. It accepts the standard Docker values — `Always`, `Missing`, `Never` — and is now enforced by the runtime. Combine with explicit digest pinning (`@sha256:…`) for maximum reproducibility in air-gapped environments.

#### Configuring a managed dependency with environment variables

Some managed dependencies are configured through environment variables — a case-sensitive collation on SQL Server, for instance. Use the `env` map on a dependency to pass container-level configuration:

```yaml
environment:
  dependencies:
    billing-db:
      type: sqlserver
      env:
        MSSQL_COLLATION: Latin1_General_CS_AS
```

**Important notes on dependency environment variables:**

- **Values follow the same syntax as service `env`**: bare numeric and boolean YAML scalars are retained as literal text; explicit null (`FOO: ~`) is rejected.
- **`${env:NAME}` is supported** — resolved from the engine process's environment at topology-build time. An unset variable fails the suite by name.
- **`${conn:…}` and `${secret:…}` are refused** — a dependency is a connection source, not a consumer, and container environment is readable via `docker inspect`.
- **Names the engine sets for that dependency type are refused** — the engine relies on those values to bring the dependency up in the shape every scenario shares, and on `minio` they are the credentials `${conn:…}` advertises to every other scenario consuming it. The refusal happens when the topology is built, before any container starts, and names the variable, the dependency and the type. The check is per type, so a name reserved for one type is unreserved on another. It runs only on the `run` path — `vouchfx validate` never builds a topology and will not report it — and is reported as **Inconclusive** — and since it refuses the suite before any container starts, it never lets the run exit 0, with or without `--fail-on-inconclusive` (#369).
- **Key order is significant** — two scenarios with the same dependency `env` keys in different order are treated as divergent and abort the suite as an **Environment error**.

The example shows a measurable outcome: `MSSQL_COLLATION: Latin1_General_CS_AS` is read by the SQL Server image and changes the reported collation from the default `SQL_Latin1_General_CP1_CI_AS`. Neither Aspire nor the engine sets this variable. Remember that a container accepts every environment variable and silently ignores ones it does not recognise, so always verify against the documentation for the dependency image you are using.

The refusal covers what the **engine** sets, not what Aspire sets. The engine reserves no names on `sqlserver` at all, so on this type every key an author writes is applied and wins the last write — including `ACCEPT_EULA` and `MSSQL_SA_PASSWORD`, which Aspire itself sets. Treat that as a hazard rather than a capability: `${conn:billing-db}` is built from the password Aspire generated, so overriding `MSSQL_SA_PASSWORD` desynchronises the running container from the connection string every consumer is handed. What that failure then looks like is not documented here because it has not been measured.

### variables

Optional constants pre-loaded into the shared context:

```yaml
variables:
  tenantId: "acme-corp"
  baseUrl: "/api/v1"
  timeout_ms: 5000
  expected_status: "active"
```

Variables are available to all steps as placeholders (e.g., `{tenantId}`). Use variables for fixed test data — ones that do not change between runs. For runtime-computed state, use step captures instead.

### steps

The **steps** section is the heart of the file: an ordered list of actions and assertions. Every step has:

- **`id`** (string, required) — Unique identifier. Used in reporting and to reference captures. Must start with a letter or underscore and contain only letters, digits, underscores, and hyphens.
- **`type`** (string, required) — The step family and provider (e.g., `http.rest`, `db-assert.postgres`). Determines which additional fields are valid.
- **`description`** (string, optional) — Human-readable explanation shown in output.
- **`capture`** (object, optional) — Extract fields from the step result into the shared context (see [Capturing and threading state forward](#capturing-and-threading-state-forward) below).
- **`verifyMode`** (string, optional) — `IMMEDIATE` (default) or `RETRY`. RETRY enables engine-owned polling with bounded backoff.
- **`timeout`** (string or number, optional) — Upper bound on step execution (e.g., `30s`, `30` for 30 seconds). For RETRY steps, bounds the polling window.
- **`continueOnFailure`** (boolean, optional) — When `true`, a failed assertion does not abort remaining steps (see [The four verdicts](#the-four-verdicts-and-continueonfailure) below).

Additional fields depend on the step type (e.g., `method`, `path`, `body` for `http.rest`; `query`, `parameters` for `db-assert.postgres`). See the [Language Reference](language-reference.md) for the full list per type.

---

## Metadata and test selection

The `vouchfx run` command can select scenarios by **tag**, **owner**, **path**, or **git change-set**. All criteria are optional and composable (AND across dimensions).

### By tag

```bash
# Run all scenarios tagged "smoke" OR "integration"
vouchfx run --tag smoke --tag integration

# All matching scenarios must have one of these tags
```

### By owner

```bash
# Run all scenarios owned by "team-a" OR "team-b"
vouchfx run --owner team-a --owner team-b
```

### By path

```bash
# Run scenarios matching the glob pattern
vouchfx run --path "**/payment/*"

# Patterns: *, **, or substring match
vouchfx run --path "smoke"  # Matches any file with "smoke" in its path
```

### By git change-set

```bash
# Run scenarios that changed since main (or another git ref)
vouchfx run --changed-since main

# Useful in CI: run only the suites affected by the current change
```

### Composition

Criteria across dimensions **AND** together:

```bash
# Run scenarios that are (tag=integration) AND (owner=team-a) AND (changed since main)
vouchfx run --tag integration --owner team-a --changed-since main
```

Within a dimension (multiple `--tag` values), criteria **OR** together.

---

## Environment: services and dependencies

A well-structured test file separates the **system under test** (what you are testing) from **dependencies** (infrastructure the system needs to function).

### Pattern: microservices with a database

```yaml
metadata:
  name: user-service-integration
  owner: team-users
  tags: [integration]

environment:
  # The system under test — the application you are testing
  services:
    user-api:
      image: myco/user-service:latest
      httpPort: 8080
      # The service may depend on the database; vouchfx ensures the
      # database is healthy before the service starts
      env:
        DB_HOST: "orders-db"
        DB_PORT: "5432"

  # Infrastructure the service needs — orchestrated by Aspire
  dependencies:
    user-db:
      type: postgres

steps:
  # Steps refer to services by name: target: user-api
  - id: create-user
    type: http.rest
    target: user-api
    # ...
```

**Key principle:** A test should contain exactly **one system under test** (or one logical group of tightly-coupled services). External services and fixtures are dependencies. This clarity makes selection and CI gating straightforward: you are testing one thing, and you want to know immediately whether it works.

### Pattern: test doubles

When a real dependency (external payment gateway, third-party API) cannot be used, declare a test double as a service:

```yaml
environment:
  services:
    # System under test
    checkout-api:
      image: myco/checkout-service:latest
      httpPort: 8080
      env:
        PAYMENT_GATEWAY_URL: "http://payment-gateway:8080"

    # Test double (WireMock, Mountebank, etc.)
    payment-gateway:
      image: wiremock/wiremock:3.0.1
      httpPort: 8080

steps:
  # Steps call the double identically to a real service
  - id: pay-with-stub
    type: http.rest
    target: payment-gateway  # Could be the real service; test logic is unchanged
    # ...
```

The double is a **visible, deliberate entry** in the environment. Swapping it for the real service later requires only a change to the environment declaration — the test logic stays the same.

### Pattern: dependencies from a private registry

Teams on a private registry (Nexus, Artifactory, ECR, ACR) or in air-gapped environments can override the default image for each dependency. Use the per-dependency `image:` field to name a fully-qualified image reference, or combine `imageRegistry` at the environment level with un-qualified references:

```yaml
metadata:
  name: order-service-in-regulated-environment
  owner: platform-team

environment:
  # Option 1: Redirect all un-qualified images to a single private mirror
  imageRegistry: nexus.corp.local/docker-mirror

  services:
    order-api:
      image: myco/order-service:v2.1.0    # Will pull from nexus.corp.local/docker-mirror/myco/order-service:v2.1.0
      httpPort: 8080
      env:
        DATABASE_URL: "${conn:orders-db}"

  dependencies:
    orders-db:
      type: postgres
      version: "16"                        # Will pull from nexus.corp.local/docker-mirror/library/postgres:16

    events:
      type: kafka
      image: nexus.corp.local:5000/platform/kafka:7.5.0    # Override: this one is pinned to a specific port and version

    cache:
      type: redis
```

The two approaches work together:

- **`imageRegistry`** (environment level) — prefixes every un-qualified image (one without a registry hostname). Use this as a blanket redirect for public images mirrored on an internal registry.
- **`image:` field** (per dependency) — explicitly names a full OCI reference. Use this to override one dependency, or to target a different registry host than the `imageRegistry` default.

Already-qualified references (those with a registry hostname) are never rewritten by `imageRegistry` — they are fetched from their specified host as-is.

---

## Configuring the system under test from the environment block

A containerised system under test frequently needs configuration at startup — database URLs, broker endpoints, API keys, feature flags. Rather than baking these into the image, the `env` map on a service passes configuration at runtime, with support for **connection references** that the engine resolves to the actual network endpoints discovered during topology build.

### Pattern: service with managed dependencies

A service often depends on databases or brokers declared elsewhere in the environment. Use `${conn:<dependency>}` or `${conn:<dependency>.<part>}` to inject the resolved connection details:

```yaml
environment:
  services:
    orders-api:
      image: myorg/orders-api:latest
      env:
        # Literal values
        APP_ENV: "test"
        LOG_LEVEL: "debug"
        # Full connection string for a dependency
        DATABASE_URL: "${conn:orders-db}"
        # Individual parts: host, port, username, password, database
        CACHE_HOST: "${conn:cache.host}"
        CACHE_PORT: "${conn:cache.port}"
        MESSAGE_BROKER_URL: "${conn:events}"
  dependencies:
    orders-db:
      type: postgres
      version: "16"
    events:
      type: kafka
    cache:
      type: redis
```

The engine resolves the connection strings **at topology-build time, in the service's network context**. Containerised services receive the container-internal hostnames and ports (e.g., the Aspire-generated logical names), not the host-published ones. This makes the topology portable: a service that talks to `${conn:orders-db}` works identically whether orders-db runs locally in Docker or in a managed cloud data platform.

### Pattern: containerised SUT with webhook callbacks

When a containerised system needs to call back to a webhook listener (a common pattern for async notifications), use the `_container` form of the listener variable to get the host-gateway address:

```yaml
environment:
  services:
    notification-system:
      image: myorg/notifications:latest
      env:
        # Pass the container-reachable form of the callback URL
        WEBHOOK_CALLBACK_URL: "{webhook-listener_container}/callbacks/delivery"

steps:
  - id: setup-webhook
    type: http.rest
    target: notification-system
    method: POST
    path: "/register"
    body:
      callbackUrl: "{webhook-listener_container}"
    expect:
      status: 200

  - id: wait-for-notification
    type: webhook-listen.http
    listener: webhook-listener
    verifyMode: RETRY
    timeout: 30s
    match:
      method: POST
      path: "/callbacks/delivery"
      bodyContains: "notification-id-123"
```

Both `{webhook-listener}` (loopback, for host-local consumers) and `{webhook-listener_container}` (host-gateway, for containerised consumers) are available in `Vars`. The engine automatically configures each containerised service with `--add-host=host.docker.internal:host-gateway`, making the container-form addresses reachable.

**See also:** [docs/02 §3.2.6](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md#326-configuring-the-system-under-test) for the complete specification of a service's `env`, connection parts, and validation rules, and [§3.2.6c](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md#326c-configuring-a-managed-dependency) for a managed dependency's. For real-world worked examples, see the [vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples) repository.

---

## Capturing and threading state forward

A **capture** extracts a field from the current step's result and stores it in the shared context (`Vars`). Later steps can reference it using `{placeholder}` syntax.

### JSONPath capture (HTTP responses)

For `http.rest` steps, use **JSONPath** to extract JSON fields:

```yaml
- id: create-resource
  type: http.rest
  target: api
  method: POST
  path: /resources
  body:
    name: "My Resource"
  expect:
    status: 201
  capture:
    resource_id: "$.id"              # Root-level field
    owner_email: "$.owner.email"     # Nested field
    first_tag: "$.tags[0]"           # Array element
    tag_count: "$.tags.length"       # Array length
```

The JSONPath expressions are standard (RFC 9535). Captures are stored in `Vars` and available as placeholders in all subsequent steps.

### XPath capture (XML responses)

For HTTP responses in XML, use **XPath**:

```yaml
- id: fetch-xml
  type: http.rest
  target: api
  method: GET
  path: /data.xml
  expect:
    status: 200
  capture:
    title: "/root/title/text()"
    count: "count(/root/items/item)"
```

### Using captured values in later steps

```yaml
- id: verify-created
  type: db-assert.postgres
  target: mydb
  query: >-
    SELECT id, name FROM resources WHERE id = @rid
  parameters:
    # {resource_id} is replaced with the captured value at execution time
    rid: "{resource_id}"
  expect:
    rowCount: 1
```

Placeholders are substituted at **execution time** (after the earlier step completes). If a capture expression fails to match (e.g., JSONPath `$.missing_field`), the step fails with a clear error.

### Placeholder and secret syntax

Within any string field (path, headers, body, query parameters, etc.), you can use:

- **`{placeholder}`** — Replaced with a captured value or a variable.
- **`${secret:env/VAR}`** — Replaced with the value of the environment variable `VAR`, resolved at execution time and redacted from output.
- **`${secret:vault/path}`** — Replaced with a secret from a Vault backend, resolved at execution time and redacted from output.

**Example:**

```yaml
steps:
  - id: fetch-data
    type: http.rest
    target: api
    method: GET
    path: "/users/{user_id}"
    headers:
      Authorization: "Bearer ${secret:env/API_TOKEN}"
    # ...
```

---

## The four verdicts and `continueOnFailure`

vouchfx distinguishes four outcomes (see `docs/01` §12.1):

| Verdict | Meaning | CI impact |
|---------|---------|-----------|
| **Pass** | All assertions passed. | Exit 0 (success) |
| **Fail** | An assertion failed — a genuine product defect. | Exit 1 (always breaks CI) |
| **EnvironmentError** | Infrastructure problem (unhealthy container, image-pull failure, seed failure). | Exit 0 by default; Exit 3 if `--fail-on-env-error` |
| **Inconclusive** | Engine could not decide (timeout on a RETRY step, unmet capture, partition); or the run hit a parse failure or executed nothing (any unreadable or malformed file, whether or not a sibling parsed; or a suite refused before execution). | Exit 0 by default; Exit 4 if `--fail-on-inconclusive`; never 0 on a parse failure, or on an Inconclusive suite refused before anything ran |

By default, **only Fail breaks CI** — environment errors and inconclusive results exit 0. Two exceptions break CI regardless of the gating flags: (1) an unconfirmable `security:` declaration, and (2) any parse failure, or an Inconclusive suite refused before any scenario executed. A run that executed nothing but carries an `EnvironmentError` — a topology that never came up — is not the second case and still exits 0 by default. This distinction lets your CI system handle each outcome independently: fail the build on a product defect, page on-call for infrastructure breakage, and escalate inconclusive results to reliability engineering.

### `continueOnFailure`

By default, a failed step aborts the remaining steps. Set `continueOnFailure: true` to record the failure but continue:

```yaml
steps:
  - id: cleanup-1
    type: http.rest
    target: api
    method: DELETE
    path: /resources/old-1
    expect:
      status: 200
    continueOnFailure: true  # Even if this fails, run the next step

  - id: cleanup-2
    type: http.rest
    target: api
    method: DELETE
    path: /resources/old-2
    expect:
      status: 200
    continueOnFailure: true

  - id: verify-cleaned
    type: db-assert.postgres
    target: mydb
    query: SELECT COUNT(*) as count FROM resources WHERE status = 'stale'
    expect:
      rowCount: 0
```

If cleanup-1 fails, cleanup-2 and verify-cleaned still run. The scenario verdict reflects all failures. Use `continueOnFailure` for cleanup, non-critical setup, or auditing — not for load-bearing test logic.

---

## Common step patterns

### HTTP request with assertions

```yaml
- id: fetch-users
  type: http.rest
  target: api
  method: GET
  path: /users
  expect:
    status: 200
  capture:
    user_count: "$.count"
```

**Fields:**
- **`target`** — Logical service name from `environment.services`.
- **`method`** — HTTP verb (GET, POST, PUT, PATCH, DELETE).
- **`path`** — URL path (may contain `{placeholder}` and `${secret:…}`).
- **`headers`** (optional) — Request headers.
- **`body`** (optional) — Request body (YAML scalar or mapping, serialised to JSON).
- **`expect`** (optional) — Assertions on response status code only (`status: <int>`).
- **`capture`** (optional) — JSONPath expressions extracting response fields for inspection in later steps.

For richer response validation (body structure, headers, etc.), use a `script.csharp` step to inspect captured values and decide Pass/Fail, or use a database assertion on the result of the call. See [Language Reference § http.rest](language-reference.md#httprest) for full details.

### Database assertion

```yaml
- id: verify-order-total
  type: db-assert.postgres
  target: orders-db
  query: >-
    SELECT total, currency FROM orders WHERE id = @order_id
  parameters:
    order_id: "{order_id}"
  expect:
    rowCount: 1
    row:
      total: "100.00"
      currency: "GBP"
  verifyMode: RETRY
  timeout: 30s
```

**Fields:**
- **`target`** — Logical dependency name from `environment.dependencies`.
- **`query`** — SQL query (may be multi-line; parameters use `@name` syntax).
- **`parameters`** (optional) — Bind values by name (replaces `@name` in the query).
- **`expect`** — Assertions on row count and row contents. Row field values are compared as strings; the database value's text form must match exactly (including formatting — e.g., "100.00" not 100.00 or "100").
- **`verifyMode`** (optional) — `RETRY` to poll until the assertion passes.
- **`timeout`** (optional) — Upper bound on step/polling duration.

See [Language Reference § db-assert.postgres](language-reference.md#db-assertpostgres) for full details.

### Message publish (Kafka)

```yaml
- id: publish-order-event
  type: mq-publish.kafka
  target: kafka
  topic: orders.created
  payload: |
    {
      "orderId": "{order_id}",
      "customerId": "cust_001",
      "total": 100.00
    }
  key: "{order_id}"
```

**Fields:**
- **`target`** — Logical Kafka dependency.
- **`topic`** — Kafka topic (may contain placeholders).
- **`payload`** — Message value (UTF-8 string, may contain placeholders and secrets).
- **`key`** (optional) — Message key (may contain placeholders).
- **`headers`** (optional) — Kafka headers.
- **`avro`** (optional) — Avro schema and record for schema-registry encoding.

### Message expectation (Kafka)

```yaml
- id: expect-event
  type: mq-expect.kafka
  target: kafka
  topic: orders.shipped
  match:
    key: "{order_id}"
    json:
      status: "shipped"
  verifyMode: RETRY
  timeout: 30s
```

**Fields:**
- **`target`** — Logical Kafka dependency.
- **`topic`** — Topic to consume from.
- **`match`** — Criteria a message must satisfy (key, headers, payloadContains, json).
- **`verifyMode`** (optional) — `RETRY` to poll until a matching message arrives.
- **`timeout`** (optional) — Upper bound on polling.
- **`avro`** (optional) — Schema-registry decoding (for Avro-encoded messages).

### Inline script

```yaml
- id: compute-total
  type: script.csharp
  code: |
    var items = (List<Item>)Vars["items"];
    var total = items.Sum(i => i.Price);
    Vars["computed_total"] = total;
```

**Fields:**
- **`code`** — C# code (Roslyn CSX). Has access to the shared `Vars` dictionary (an `IDictionary<string, object?>`). Libraries available: `System.Text.Json`, `Polly` v8, `JsonPath.Net`, `JsonSchema.Net`, and standard .NET runtime types.

Scripts run within the same compiled delegate as all other steps. State threads forward via `Vars` using dictionary indexing and casts (e.g. `var x = (Type)Vars["key"];` or `Vars.TryGetValue("key", out var v)`).

### Webhook listener

```yaml
- id: wait-for-notification
  type: webhook-listen.http
  listener: notification-receiver
  match:
    method: POST
    path: "/webhook/.*"
    bodyContains: "notified"
  timeout: 60s
  capture:
    notification: "$"
```

**Fields:**
- **`listener`** — Logical name of the webhook receiver (the engine stands up a Kestrel listener on an unguessable path and stages its URL at `svc::listener-name`).
- **`match`** — Criteria the inbound request must satisfy (method, path, headers, bodyContains).
- **`timeout`** (optional) — Upper bound on how long to wait for a matching request.
- **`capture`** (optional) — Extract the request body or headers.

---

## Multi-step workflows: the reference scenario

The canonical vouchfx scenario crosses multiple layers: REST call → Kafka publish → database mutation → outbound webhook. Here's a worked example:

```yaml
metadata:
  name: order-fulfillment-e2e
  owner: team-orders
  tags: [integration, e2e, critical]
  description: >-
    Place an order via REST, verify it's published to Kafka, check the
    database, and assert an outbound notification is delivered.

environment:
  services:
    order-api:
      image: myco/order-service:latest
      httpPort: 8080
      env:
        KAFKA_BROKERS: "kafka:9092"
        DB_HOST: "orders-db"

  dependencies:
    orders-db:
      type: postgres
    kafka:
      type: kafka

  seed:
    orders-db:
      sql: [ "fixtures/init.sql" ]

variables:
  expected_status: "pending"

steps:
  # Step 1: Create an order via REST
  - id: create-order
    type: http.rest
    target: order-api
    method: POST
    path: /orders
    body:
      customerId: "cust_001"
      items: [ { sku: "SKU-001", qty: 2, price: 50.00 } ]
    expect:
      status: 201
    capture:
      order_id: "$.id"
      created_at: "$.createdAt"

  # Step 2: Verify the order is in the database
  - id: verify-order-in-db
    type: db-assert.postgres
    target: orders-db
    query: >-
      SELECT id, customer_id, status, total
      FROM orders
      WHERE id = @order_id
    parameters:
      order_id: "{order_id}"
    expect:
      rowCount: 1
      row:
        customer_id: "cust_001"
        status: "{expected_status}"

  # Step 3: Expect the Kafka event (with polling, in case there is a slight lag)
  - id: expect-order-created-event
    type: mq-expect.kafka
    target: kafka
    topic: orders.created
    match:
      key: "{order_id}"
      json:
        orderId: "{order_id}"
        customerId: "cust_001"
    verifyMode: RETRY
    timeout: 10s
    capture:
      event_payload: "$"

  # Step 4: Stand up a webhook listener and wait for a notification
  - id: wait-for-notification
    type: webhook-listen.http
    listener: order-notification
    match:
      method: POST
      path: "/webhook/order-status"
      bodyContains: "order_created"
    timeout: 30s
    capture:
      notification_body: "$"

  # Step 5: Script to compute a derived value (e.g., total price)
  - id: compute-expected-total
    type: script.csharp
    code: |
      var items = new[] { 50.00m, 50.00m };  // Qty 2 at $50 each
      var total = items.Sum();
      Vars["expected_total"] = total;

  # Step 6: Final assertion — verify the computed total in the database
  - id: verify-order-total
    type: db-assert.postgres
    target: orders-db
    query: >-
      SELECT total FROM orders WHERE id = @order_id
    parameters:
      order_id: "{order_id}"
    expect:
      rowCount: 1
      row:
        total: 100.00
```

**Pattern breakdown:**

1. **REST step** (create-order) → captures `order_id`.
2. **Database assertion** (verify-order-in-db) → uses captured `{order_id}`.
3. **Kafka expectation** (expect-order-created-event) → polls for the event with `verifyMode: RETRY`.
4. **Webhook listener** (wait-for-notification) → waits for an inbound callback.
5. **Script** (compute-expected-total) → compute test data or derived values.
6. **Database assertion** (verify-order-total) → final verification.

Each step threads state forward via captures and placeholders. The verdict is **Pass** only if all steps succeed; any failure (assertion, timeout, infrastructure problem) is recorded clearly.

---

## State isolation per store

Between **sequential** scenarios that share a single topology, the engine automatically resets the following dependency types. Each uses a store-appropriate mechanism that clears data whilst preserving structure (tables, indexes, mappings). **Parallel** scenarios are unaffected — each receives its own topology and fresh containers, so isolation holds by construction.

| Dependency kind | Reset mechanism | Details |
|---|---|---|
| `postgres` | Respawn DELETE order | Tables and schemas preserved; identity/sequence values NOT reset |
| `sqlserver` | Respawn DELETE order | Temporal (system-versioned) tables handled; identity values NOT reset |
| `mysql` | Respawn DELETE order | Reset scoped to the dependency's own database; auto-increment values NOT reset |
| `mongodb` | Document deletion per collection | Collections and indexes preserved; capped and time-series collections fail reset (environment error) |
| `redis` | FLUSHDB on designated database | Only the discovered database cleared; other databases on the same instance untouched |
| `elasticsearch` | Delete-by-query across open indices | Mappings, settings and hidden indices preserved; per-document failures fail the reset |
| Brokers (`kafka`, `rabbitmq`, `nats`, `azureservicebus`) | Not applicable | Messages are consumed/scanned per step; scope topics/queues/subjects per suite to avoid cross-scenario message leakage |
| `dynamodb`, `minio` | Not reset | Add explicit cleanup steps (e.g. `script.csharp` with AWS SDK calls) to clear state between scenarios |

A failed reset surfaces as an **environment error naming the dependency** — never a test failure. This distinction is critical: if a dependency became unhealthy mid-suite, the user sees `EnvironmentError`, not a false test failure. Check the event stream to diagnose reset failures.

Seed applies to the first scenario only; subsequent scenarios receive cleared data (seeded reference rows are also cleared). See the [troubleshooting guide](troubleshooting.md) for reset failure diagnostic details.

---

## See also

- **[Getting Started](getting-started.md)** — Your first vouchfx test in 60 minutes.
- **[Recipes](recipes.md)** — Task-oriented examples (seeding, test doubles, secrets, CI integration).
- **[Language Reference](language-reference.md)** — Complete per-step-type field reference.
- **[Troubleshooting](troubleshooting.md)** — Real failure modes and fixes.
- **[Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md)** — How the system works.
- **[YAML DSL Specification](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)** — The full grammar and schema.
