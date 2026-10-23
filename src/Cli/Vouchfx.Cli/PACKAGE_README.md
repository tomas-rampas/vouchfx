# vouchfx

**End-to-end integration testing for distributed systems, authored in YAML.**

vouchfx compiles declarative `.e2e.yaml` tests into memory-safe C# (via Roslyn), orchestrates the container topology your system needs with **.NET Aspire + Testcontainers**, and executes one business transaction as it crosses a REST call, a message-broker event, a database mutation and an outbound webhook — the seams where distributed systems actually break. It is not a unit-test framework and not a UI/browser tool.

## Install

Stable releases are available on NuGet.org. Install or upgrade to the latest stable CLI:

```bash
dotnet tool install --global vouchfx

# Already installed? Upgrade to the latest stable release
dotnet tool update --global vouchfx
```

These commands select the latest stable release. For intentional preview validation, select the matching prerelease version explicitly.

Requires the .NET 8 SDK and a running Docker engine with Linux containers. This package includes
the matching Aspire DCP tools for Windows x64, Linux x64 (glibc), macOS x64 and macOS ARM64;
no source checkout or Aspire restore is required. Keep the complete installed payload. An explicit
`ASPIRE_DCP_PATH` still selects an external matching tools directory.

## A test, in shape

```yaml
metadata:
  name: order-flow
environment:
  services:
    app: { image: my-org/orders-api:latest }
  dependencies:
    orders-db: { type: postgres }
    events:    { type: kafka }
steps:
  - id: create-order
    type: http.rest
    target: app
    method: POST
    path: /orders
    body: '{"customerId":"c-1","amount":49.99}'
    expect: { status: 201 }
    capture: { orderId: $.id }

  - id: assert-row
    type: db-assert.postgres
    target: orders-db
    query: "SELECT status FROM orders WHERE id = '{orderId}'"
    verifyMode: RETRY
    timeout: 15s
    expect: { rows: 1 }
```

```bash
vouchfx run ./tests
```

## What ships in the box

**Twenty-five Core providers across eleven step families**: HTTP (REST, SOAP) · database assertions (PostgreSQL, MySQL, SQL Server, MongoDB, DynamoDB) · message publish/expect (Kafka, RabbitMQ, NATS, Azure Service Bus, Redis Streams) · cache and search (Redis, Elasticsearch) · object storage (S3) · Prometheus metrics · OTLP distributed-trace assertions · SMTP mail capture · webhook listeners · inline C# scripting.

Plus: engine-owned RETRY polling (you never write `Thread.Sleep`), `${secret:…}` references resolved at execution time and redacted at the source, a four-verdict taxonomy where **only `Fail` breaks CI by default** (environment errors and timeouts are kept distinct, with five narrow exceptions that prevent exit 0 — a parse failure, a typed authoring/schema/compilation refusal, an Inconclusive suite with no executed scenario, a provider- or engine-surface refusal, and an unconfirmable `security:` declaration), scenario selection by tag/owner/path/git-change-set, parallel topology-per-scenario runs, watch mode, and reporting as a schema-versioned JSON Lines event stream rendered to terminal, self-contained HTML and JUnit XML. Also includes `vouchfx plan` for deterministic coverage-and-gap analysis of your declared suites against run history and available providers; `vouchfx scaffold` to generate test skeletons from structured intent; and `vouchfx validate`, `list`, and `schema` for Docker-free compile-time tooling.

## Learn more

- **Documentation**: <https://vouchfx.io/> — getting started (60-minute path), recipes, the language reference, and the architecture blueprint.
- **Source**: <https://github.com/tomas-rampas/vouchfx> (Apache-2.0)
- **Community provider hub**: <https://github.com/tomas-rampas/vouchfx-providers> — Core and Community providers, the Vouched badge rubric, and examples.

## Related packages

`vouchfx` is the CLI tool only — it runs `.e2e.yaml` suites and does not need to be
referenced from code. If you're **writing a new step provider** instead of running
suites, you want these packages, not this one:

- **[`Vouchfx.Sdk`](https://www.nuget.org/packages/Vouchfx.Sdk)** — the frozen v1 provider
  contract (`IStepProvider`, `IStepBinder<T>`, `IStepValidator<T>`, `IStepCompiler<T>`,
  `IResourceContributor<T>`). Reference this from your provider project.
- **[`Vouchfx.Sdk.Testing`](https://www.nuget.org/packages/Vouchfx.Sdk.Testing)** — an
  out-of-repo test harness that runs a single-step `.e2e.yaml` scenario end to end
  without Docker. Reference this from your provider's test project.

> **Release channels**: Stable Core CLI and SDK packages are published on NuGet.org; preview versions are for validation before their corresponding stable release. The v1 language schema, provider SDK and event-wire contracts are frozen; within the v1.x series evolution is additive only.
