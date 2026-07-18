# vouchfx Language Reference

> **Generated file — do not edit by hand.**
> This reference is generated from the composed `v1` JSON Schema (the exact contract the compiler validates against),
> so it can never drift from what vouchfx actually accepts. See *Regenerating this file* at the foot of this document.

Root schema for a vouchfx .e2e.yaml test file (language schema v1).

Each test step is a typed action or assertion. Every step shares a set of **common fields** (documented first), plus the **type-specific fields** of its `type` discriminator (the `<family>.<provider>` value, e.g. `http.rest` or `db-assert.postgres`). The sections below list, for each step type, its required and optional fields with their types and descriptions.

**Schema version:** `v1`

## Common step fields

These fields may appear on **any** step, regardless of its `type`. `id` and `type` are required on every step; the rest are optional.

| Field | Required | Type | Description |
| --- | --- | --- | --- |
| `id` | yes | `string` | A unique identifier for the step within the file; used in reporting and failure messages. Must start with a letter or underscore and contain only letters, digits, underscores, and hyphens. |
| `type` | yes | `string` | The kind of step. Must be in dotted family.provider notation, e.g. http.rest or db-assert.postgres; a bare family name (e.g. http) is not accepted. |
| `description` | no | `string` | A short human-readable explanation shown in test output. |
| `capture` | no | `object` | A map of variable names to extractor expressions that write values from this step's result into the shared context. |
| `verifyMode` | no | `string` | Either IMMEDIATE (default) or RETRY. RETRY instructs the engine to poll with bounded exponential backoff. |
| `timeout` | no | `string` \| `number` | An upper bound on how long the step may take, expressed as a duration string (e.g. 30s) or a number of seconds — enforced for every verify mode. An IMMEDIATE step that exceeds it resolves as Inconclusive (step-timeout, never Fail); where the provider's emitted body sets a built-in transport timeout (the HTTP, AWS and SQL command-timeout conventions), the declared value replaces it as the governing bound. For a RETRY step it bounds the polling window. |
| `continueOnFailure` | no | `boolean` | When true, a failed assertion is recorded but does not abort the remaining steps. Defaults to false. |

## Step types

Registered step types (25):

- [`cache-assert.elasticsearch`](#cache-assertelasticsearch)
- [`cache-assert.redis`](#cache-assertredis)
- [`db-assert.dynamodb`](#db-assertdynamodb)
- [`db-assert.mongodb`](#db-assertmongodb)
- [`db-assert.mysql`](#db-assertmysql)
- [`db-assert.postgres`](#db-assertpostgres)
- [`db-assert.sqlserver`](#db-assertsqlserver)
- [`http.rest`](#httprest)
- [`http.soap`](#httpsoap)
- [`mail-expect.smtp`](#mail-expectsmtp)
- [`metrics-assert.prometheus`](#metrics-assertprometheus)
- [`mq-expect.azureservicebus`](#mq-expectazureservicebus)
- [`mq-expect.kafka`](#mq-expectkafka)
- [`mq-expect.nats`](#mq-expectnats)
- [`mq-expect.rabbitmq`](#mq-expectrabbitmq)
- [`mq-expect.redis`](#mq-expectredis)
- [`mq-publish.azureservicebus`](#mq-publishazureservicebus)
- [`mq-publish.kafka`](#mq-publishkafka)
- [`mq-publish.nats`](#mq-publishnats)
- [`mq-publish.rabbitmq`](#mq-publishrabbitmq)
- [`mq-publish.redis`](#mq-publishredis)
- [`script.csharp`](#scriptcsharp)
- [`storage-assert.s3`](#storage-asserts3)
- [`trace-expect.otlp`](#trace-expectotlp)
- [`webhook-listen.http`](#webhook-listenhttp)

### `cache-assert.elasticsearch`

Set `type: cache-assert.elasticsearch` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | Expected result-set characteristics. |
| `index` | `string` | Elasticsearch index to query. |
| `target` | `string` | Logical name of the elasticsearch dependency declared under environment.dependencies whose HTTP API this step queries. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `query` | `string` | Full Elasticsearch Query DSL JSON body (the entire request body, e.g. '{"query":{"match":{"status":"active"}}}'). When absent a match_all query is used. May contain {placeholder} tokens resolved at execution time. |

### `cache-assert.redis`

Set `type: cache-assert.redis` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | Assertion block. Exactly one member applies per operation: value (get/hget), exists (exists/ttl), or length (hlen/llen/scard). |
| `key` | `string` | The Redis key to inspect. May contain {placeholder} tokens resolved at step-execution time. |
| `operation` | `string` | The Redis read operation. get/hget assert on 'expect.value'; exists asserts on 'expect.exists'; ttl asserts on 'expect.exists' as a has-a-positive-TTL presence check (exact/lower-bound TTLs are deliberately unsupported to avoid CI flakiness); hlen/llen/scard assert on 'expect.length'. Values are case-insensitive. |
| `target` | `string` | Logical name of the redis dependency to inspect, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `field` | `string` | The hash field name for the hget operation (required for hget only). May contain {placeholder} tokens. |

### `db-assert.dynamodb`

Set `type: db-assert.dynamodb` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | Assertion block declaring the expected GetItem outcome. |
| `key` | `string` | A flat JSON object template naming the primary key (partition key, optionally plus a sort key) for a GetItem call, e.g. {"orderId":"{orderId}"}. Each top-level value becomes an S/N/BOOL DynamoDB attribute; nested objects/arrays/null are not supported. May contain {placeholder} tokens inside JSON string values. |
| `table` | `string` | Name of the DynamoDB table to query. May contain {placeholder} tokens. |
| `target` | `string` | Logical name of the dynamodb dependency to query, as declared under environment.dependencies. |

### `db-assert.mongodb`

Set `type: db-assert.mongodb` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `collection` | `string` | Name of the MongoDB collection to query. |
| `expect` | `object` | Assertion block declaring the expected query outcome. At least one of count or document must be specified. |
| `filter` | `string` | JSON filter document. May contain {placeholder} tokens resolved at runtime. |
| `target` | `string` | Logical name of the mongodb dependency to query, as declared under environment.dependencies. |

### `db-assert.mysql`

Set `type: db-assert.mysql` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | Assertion block declaring the expected query outcome. At least one of rowCount or row must be specified. |
| `query` | `string` | The SQL query to execute. May be a multi-line literal. |
| `target` | `string` | Logical name of the mysql dependency to query, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `parameters` | `object` | Optional map of SQL parameter names (without leading '@') to their string values. |

### `db-assert.postgres`

Set `type: db-assert.postgres` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | Assertion block declaring the expected query outcome. At least one of rowCount or row must be specified. |
| `query` | `string` | The SQL query to execute. May be a multi-line literal. |
| `target` | `string` | Logical name of the postgres dependency to query, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `parameters` | `object` | Optional map of SQL parameter names (without leading '@') to their string values. |

### `db-assert.sqlserver`

Set `type: db-assert.sqlserver` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | Assertion block declaring the expected query outcome. At least one of rowCount or row must be specified. |
| `query` | `string` | The SQL query to execute. May be a multi-line literal. |
| `target` | `string` | Logical name of the sqlserver dependency to query, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `parameters` | `object` | Optional map of SQL parameter names (without leading '@') to their string values. |

### `http.rest`

Set `type: http.rest` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `method` | `string` | The HTTP verb. |
| `path` | `string` | The request path; may contain variable placeholders. |
| `target` | `string` | Logical name of the service to call, as declared under environment.services. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `body` | `any` | Optional request body, given inline as YAML and serialised to JSON. |
| `expect` | `object` | Optional assertion block applied to the HTTP response. |
| `headers` | `object` | Optional map of request header names to values. |

### `http.soap`

Set `type: http.soap` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `envelope` | `string` | The FULL SOAP request envelope XML, given as a raw template string (no auto-wrapping). Sent as Content-Type: text/xml; charset=utf-8. May contain {placeholder} and ${secret:...} tokens. |
| `path` | `string` | The request path; may contain {placeholder} and ${secret:...} tokens. |
| `target` | `string` | Logical name of the service to call, as declared under environment.services. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `action` | `string` | Optional SOAPAction header value (SOAP 1.1 convention), sent quoted per spec. May contain {placeholder} and ${secret:source/path} tokens. |
| `expect` | `object` | Optional assertion block applied to the SOAP response. |

### `mail-expect.smtp`

Set `type: mail-expect.smtp` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | The expectation block: how many messages must match the criteria. |
| `target` | `string` | Logical name of the mailpit dependency (declared under environment.dependencies) whose HTTP API this step queries. |

### `metrics-assert.prometheus`

Scrapes a Prometheus text-exposition endpoint (normally the SUT's own /metrics) and asserts on one metric's numeric value, optionally scoped by a label subset. Exactly one sample must match the declared metric name plus every declared label; zero or more than one match is a Fail (an under-specified label set is an authoring error, never silently resolved).

Set `type: metrics-assert.prometheus` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `expect` | `object` | The value assertion block. At least one of value, min, or max must be declared; all three may combine. Each is a decimal string (may contain {placeholder} / ${secret:source/path} tokens) parsed as a double at execution time. |
| `metric` | `string` | The Prometheus sample (metric) name to select, e.g. 'orders_processed_total'. May contain {placeholder} and ${secret:source/path} tokens. |
| `target` | `string` | Logical name of the service to scrape, as declared under environment.services — normally the system under test. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `labels` | `object` | Optional map of required label name to expected value. A sample matches only when it carries every declared label with exactly the expected value (a subset match — the sample may carry additional labels). Values may contain {placeholder} and ${secret:source/path} tokens. |
| `path` | `string` | The scrape path. May contain {placeholder} and ${secret:source/path} tokens. Defaults to '/metrics' when omitted. |

### `mq-expect.azureservicebus`

Non-destructively peeks an Azure Service Bus queue or topic subscription and verifies at least one message matches the declared expectations. Designed for verifyMode: RETRY — the engine retries on Fail until a matching message is found or the timeout is reached. Note: each attempt scans at most 100 messages (PeekMessagesAsync window); a match beyond the first 100 retained messages in the entity will not be found.

Set `type: mq-expect.azureservicebus` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `target` | `string` | Logical name of the azureservicebus dependency to peek, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `expectPayloadContains` | `string` | Optional substring the message body must contain for a match. May contain {placeholder} and ${secret:source/path} tokens. |
| `expectProperties` | `object` | Optional application-property key=value pairs all of which must be present on the matched message. Values may contain {placeholder} and ${secret:source/path} tokens. |
| `queue` | `string` | The source queue to peek. Set 'queue' for queue-based messaging, or 'topic'+'subscription' for topic-based messaging. |
| `subscription` | `string` | The subscription on the topic to peek. Required when 'topic' is set. May contain {placeholder} substitution tokens. |
| `topic` | `string` | The source topic (requires 'subscription' to also be set). May contain {placeholder} substitution tokens. |

### `mq-expect.kafka`

Set `type: mq-expect.kafka` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `match` | `object` | The criteria a consumed message must satisfy. At least one criterion (key, headers, payloadContains, or json) must be declared. |
| `target` | `string` | Logical name of the kafka dependency to consume from, as declared under environment.dependencies. |
| `topic` | `string` | The Kafka topic to consume the message from. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `avro` | `object` | Optional Avro / schema-registry decoding. When present, the consumed message is Avro-decoded to a GenericRecord, converted to a JSON string, and the existing match criteria run against that JSON. |

### `mq-expect.nats`

Asserts that a message matching the declared criteria is present on a NATS JetStream subject. The consumer scans from the beginning of the retained log on every attempt (DeliverPolicy.All ordered consumer), mirroring mq-expect.kafka retained-log behaviour. IMPORTANT: do NOT share a single nats dependency across scenarios that assert on the same subject — retained messages from prior runs produce a false Pass. Use verifyMode: RETRY to poll until the message arrives.

Set `type: mq-expect.nats` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `match` | `object` | The criteria a fetched message must satisfy. At least one criterion (payloadContains or json) must be declared. |
| `subject` | `string` | The NATS JetStream subject to filter messages on. |
| `target` | `string` | Logical name of the nats dependency to consume from, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `stream` | `string` | Optional JetStream stream name. When absent, derived from 'subject' (same rule as mq-publish.nats). |

### `mq-expect.rabbitmq`

Set `type: mq-expect.rabbitmq` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `match` | `object` | The criteria a consumed message must satisfy. At least one criterion (payloadContains, headers, or json) must be declared. |
| `queue` | `string` | The AMQP queue to consume messages from. |
| `target` | `string` | Logical name of the rabbitmq dependency to consume from, as declared under environment.dependencies. |

### `mq-expect.redis`

Asserts that a message matching the declared criteria is present on a Redis Stream, scanned from the beginning via XRANGE <stream> - + COUNT 10000 on every attempt (only entries carrying a field named 'payload' are matched — the convention mq-publish.redis writes). IMPORTANT: do NOT share a single redis dependency across scenarios that assert on the same stream — entries from prior runs produce a false Pass. A stream retaining more than 10 000 entries only has its first 10 000 (oldest first) inspected. Use verifyMode: RETRY to poll until the message arrives.

Set `type: mq-expect.redis` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `match` | `object` | The criteria a fetched message must satisfy. At least one criterion (payloadContains or json) must be declared. |
| `stream` | `string` | The Redis Stream key to scan via XRANGE. |
| `target` | `string` | Logical name of the redis dependency to consume from, as declared under environment.dependencies. |

### `mq-publish.azureservicebus`

Publishes one UTF-8 message to an Azure Service Bus queue or topic. A Pass verdict confirms the send was accepted by the broker. Verify with a following mq-expect.azureservicebus step.

Set `type: mq-publish.azureservicebus` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `payload` | `string` | The message body sent as UTF-8 bytes. May contain {placeholder} and ${secret:source/path} tokens. |
| `target` | `string` | Logical name of the azureservicebus dependency to publish to, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `properties` | `object` | Optional application properties to attach to the message (string key=value pairs). Values may contain {placeholder} and ${secret:source/path} tokens. |
| `queue` | `string` | The target queue name. Exactly one of 'queue' or 'topic' must be set. May contain {placeholder} substitution tokens. |
| `topic` | `string` | The target topic name. Exactly one of 'queue' or 'topic' must be set. May contain {placeholder} substitution tokens. |

### `mq-publish.kafka`

Set `type: mq-publish.kafka` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `payload` | `string` | The message payload sent as the Kafka message value. A UTF-8 string (literal or inline JSON). May contain {placeholder} and ${secret:source/path} tokens. |
| `target` | `string` | Logical name of the kafka dependency to publish to, as declared under environment.dependencies. |
| `topic` | `string` | The Kafka topic to publish the message to. May contain {placeholder} and ${secret:source/path} tokens. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `avro` | `object` | Optional Avro / schema-registry encoding. When present, the message value is built as an Avro GenericRecord from 'schema' + 'record' and produced via the Confluent Schema Registry Avro serializer; the plain 'payload' is ignored. |
| `headers` | `object` | Optional map of message header names to their string values. |
| `key` | `string` | Optional message key. May contain {placeholder} and ${secret:source/path} tokens. |

### `mq-publish.nats`

Publishes one UTF-8 message to a NATS JetStream subject. A Pass verdict confirms the publish was accepted by the server (JetStream ack); delivery is NOT further confirmed. Verify delivery with a following mq-expect.nats step.

Set `type: mq-publish.nats` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `payload` | `string` | The message payload sent as UTF-8 bytes. May contain {placeholder} and ${secret:source/path} tokens. |
| `subject` | `string` | The NATS JetStream subject to publish to. May contain {placeholder} and ${secret:source/path} tokens. |
| `target` | `string` | Logical name of the nats dependency to publish to, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `stream` | `string` | Optional NATS JetStream stream name. When absent, derived from 'subject' by uppercasing and replacing non-alphanumeric characters with underscores (consecutive underscores collapsed). |

### `mq-publish.rabbitmq`

A Pass verdict confirms hand-off to the broker client; delivery is NOT confirmed (publisher confirms are a post-v1 feature). Verify delivery with a following mq-expect.rabbitmq step.

Set `type: mq-publish.rabbitmq` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `payload` | `string` | The message payload sent as the AMQP message body (UTF-8). May contain {placeholder} and ${secret:source/path} tokens. |
| `routingKey` | `string` | The AMQP routing key. For the default exchange this is the queue name. May contain {placeholder} and ${secret:source/path} tokens. |
| `target` | `string` | Logical name of the rabbitmq dependency to publish to, as declared under environment.dependencies. |

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `exchange` | `string` | Optional AMQP exchange name. Empty or absent routes to the default exchange. May contain {placeholder} and ${secret:source/path} tokens. |
| `headers` | `object` | Optional map of AMQP message header names to their string values. |

### `mq-publish.redis`

Publishes one UTF-8 message to a Redis Stream via XADD, carried under the canonical 'payload' stream field. A Pass verdict confirms the entry was appended to the stream; delivery to a consumer is NOT further confirmed. Verify delivery with a following mq-expect.redis step.

Set `type: mq-publish.redis` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `payload` | `string` | The message payload, written as the UTF-8 string value of the canonical 'payload' stream field. May contain {placeholder} and ${secret:source/path} tokens. |
| `stream` | `string` | The Redis Stream key to XADD to. May contain {placeholder} and ${secret:source/path} tokens. XADD creates the stream automatically when it does not yet exist. |
| `target` | `string` | Logical name of the redis dependency to publish to, as declared under environment.dependencies. |

### `script.csharp`

Set `type: script.csharp` to use this step.

**Optional fields**

| Field | Type | Description |
| --- | --- | --- |
| `code` | `string` | Inline C# code block executed inside the compiled CSX submission. Has access to the shared Vars dictionary. Mutually exclusive with 'file'. |
| `file` | `string` | Path to an external .csx file, resolved relative to the .e2e.yaml file's directory. Read once at compile time and spliced verbatim, exactly like 'code'. Mutually exclusive with 'code'. |

### `storage-assert.s3`

HEADs (and, if declared, GETs) an S3-compatible object and asserts on its existence, size, content type, metadata, and/or body digest/substring.

Set `type: storage-assert.s3` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `bucket` | `string` | The S3 bucket name. May contain {placeholder} and ${secret:source/path} tokens. |
| `expect` | `object` | The assertion block declaring the expected object state. exists:false excludes every content expectation; size and minSize are mutually exclusive. |
| `key` | `string` | The S3 object key. May contain {placeholder} and ${secret:source/path} tokens. |
| `target` | `string` | Logical name of the minio dependency to query, as declared under environment.dependencies. |

### `trace-expect.otlp`

Set `type: trace-expect.otlp` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `match` | `object` | The criteria a captured span must satisfy. 'traceId' is REQUIRED — this family asserts the causal chain of a SPECIFIC transaction, not a general shape any span might match; service/spanName/attributes are optional refinements layered on top of it. |
| `receiver` | `string` | Logical name of the host-owned OTLP/HTTP receiver whose captured spans this step asserts against. The engine stands the receiver up and stages its base URL at svc::<receiver> (and at the plain <receiver> Vars key so an earlier step can hand it to the SUT's OTel SDK configuration, e.g. OTEL_EXPORTER_OTLP_ENDPOINT: "{svc::<receiver>}"). |

### `webhook-listen.http`

Set `type: webhook-listen.http` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `listener` | `string` | Logical name of the host-owned webhook listener whose captured inbound requests this step asserts against. The engine stands the listener up and stages its URL at svc::<listener> (and at the plain <listener> Vars key so an earlier step can interpolate {<listener>}). |
| `match` | `object` | The criteria a captured inbound request must satisfy. At least one criterion (method, path, headers, or bodyContains) must be declared. |

## Regenerating this file

This file is generated from the composed JSON Schema by `LanguageReferenceGenerator.Generate(...)` and frozen by the `LanguageReferenceGoldenTests` golden gate. When the schema legitimately changes (a provider adds an optional field, say), regenerate this file rather than editing it by hand:

```bash
# 1. Run the golden gate; on drift it prints the first differing line.
dotnet test tests/Vouchfx.Engine.Compilation.Tests \
  --filter "FullyQualifiedName~LanguageReferenceGoldenTests"

# 2. To regenerate, set the environment variable below and re-run the gate;
#    it rewrites docs/language-reference.md from the freshly-composed schema.
#    Review the diff, then commit.
VOUCHFX_REGEN_LANGUAGE_REFERENCE=1 dotnet test tests/Vouchfx.Engine.Compilation.Tests \
  --filter "FullyQualifiedName~LanguageReferenceGoldenTests"
```
