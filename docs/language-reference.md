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
| `type` | yes | `string` | The kind of step in dotted family.provider notation, e.g. http.rest or db-assert.postgres. |
| `description` | no | `string` | A short human-readable explanation shown in test output. |
| `capture` | no | `object` | A map of variable names to extractor expressions that write values from this step's result into the shared context. |
| `verifyMode` | no | `string` | Either IMMEDIATE (default) or RETRY. RETRY instructs the engine to poll with bounded exponential backoff. |
| `timeout` | no | `string` \| `number` | An upper bound on how long the step may take, expressed as a duration string (e.g. 30s) or a number of seconds. |
| `continueOnFailure` | no | `boolean` | When true, a failed assertion is recorded but does not abort the remaining steps. Defaults to false. |

## Step types

Registered step types (8):

- [`db-assert.mongodb`](#db-assertmongodb)
- [`db-assert.postgres`](#db-assertpostgres)
- [`db-assert.sqlserver`](#db-assertsqlserver)
- [`http.rest`](#httprest)
- [`mq-expect.kafka`](#mq-expectkafka)
- [`mq-publish.kafka`](#mq-publishkafka)
- [`script.csharp`](#scriptcsharp)
- [`webhook-listen.http`](#webhook-listenhttp)

### `db-assert.mongodb`

Set `type: db-assert.mongodb` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `collection` | `string` | Name of the MongoDB collection to query. |
| `expect` | `object` | Assertion block declaring the expected query outcome. At least one of count or document must be specified. |
| `filter` | `string` | JSON filter document. May contain {placeholder} tokens resolved at runtime. |
| `target` | `string` | Logical name of the mongodb dependency to query, as declared under environment.dependencies. |

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

### `script.csharp`

Set `type: script.csharp` to use this step.

**Required fields**

| Field | Type | Description |
| --- | --- | --- |
| `code` | `string` | Inline C# code block executed inside the compiled CSX submission. Has access to the shared Vars dictionary. |

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
dotnet test tests/Platform.Engine.Compilation.Tests \
  --filter "FullyQualifiedName~LanguageReferenceGoldenTests"

# 2. To regenerate, set the environment variable below and re-run the gate;
#    it rewrites docs/language-reference.md from the freshly-composed schema.
#    Review the diff, then commit.
VOUCHFX_REGEN_LANGUAGE_REFERENCE=1 dotnet test tests/Platform.Engine.Compilation.Tests \
  --filter "FullyQualifiedName~LanguageReferenceGoldenTests"
```
