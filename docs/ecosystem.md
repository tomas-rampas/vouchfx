# The vouchfx ecosystem

vouchfx is a coordinated ecosystem of four repositories and associated documentation sites. This page maps the landscape, so you can find what you need and understand how the pieces fit together.

## Overview

**vouchfx is one engine and three companion repositories.** The engine lives in the main repository and is the declarative YAML platform itself. The three companions host reusable providers, production-grade sample applications, and an opt-in telemetry backend.

| Repository | What it is | Site | Source |
|---|---|---|---|
| **vouchfx** (main) | The engine: compiler, orchestration, CLI, provider SDK, core providers, and documentation | [https://tomas-rampas.github.io/vouchfx/](https://tomas-rampas.github.io/vouchfx/) | [github.com/tomas-rampas/vouchfx](https://github.com/tomas-rampas/vouchfx) |
| **vouchfx-providers** | Community provider hub: registry, Vouched badge, conformance testing, examples | [https://tomas-rampas.github.io/vouchfx-providers/](https://tomas-rampas.github.io/vouchfx-providers/) | [github.com/tomas-rampas/vouchfx-providers](https://github.com/tomas-rampas/vouchfx-providers) |
| **vouchfx-samples** | Four production-grade sample applications with complete test suites in C#, Python, Node.js and Java | [https://tomas-rampas.github.io/vouchfx-samples/](https://tomas-rampas.github.io/vouchfx-samples/) | [github.com/tomas-rampas/vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples) |
| **vouchfx-telemetry-backend** | Opt-in telemetry backend: schema, deployment, verification, self-hosting guide | [https://tomas-rampas.github.io/vouchfx-telemetry-backend/](https://tomas-rampas.github.io/vouchfx-telemetry-backend/) | [github.com/tomas-rampas/vouchfx-telemetry-backend](https://github.com/tomas-rampas/vouchfx-telemetry-backend) |

## The vouchfx engine

**The main repository** — where the platform lives.

The engine comprises the YAML→AST→C#→Roslyn compiler, the Aspire/Testcontainers orchestration layer, the five-layer architecture, twenty-five Core providers across eleven families (HTTP, databases, message publishing and consumption, caches, storage, metrics, traces, mail, webhooks, scripts), the CLI, and the full set of design documentation.

**Start here:**
- **[Getting started](https://tomas-rampas.github.io/vouchfx/docs/getting-started.html)** — 60-minute path to your first PASS
- **[Recipes](https://tomas-rampas.github.io/vouchfx/docs/recipes.html)** — Task-oriented, runnable patterns
- **[Technical Architecture Blueprint](https://tomas-rampas.github.io/vouchfx/docs/01_Technical_Architecture_and_Engineering_Blueprint.html)** — The five layers, orchestration, memory model, and the frozen provider contract
- **[YAML DSL Specification](https://tomas-rampas.github.io/vouchfx/docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.html)** — The `.e2e.yaml` grammar and VSCode extension

## The community provider hub

**For when you need a provider the engine doesn't bundle.**

Two governance tiers (Core and Community) plus the maintainer-awarded Vouched badge. Submit your own provider via pull request and have it conformance-tested and listed.

**Start here:**
- **[Consuming a provider](https://tomas-rampas.github.io/vouchfx-providers/docs/consuming-a-provider.html)** — How to use a community provider in your suites
- **[Implementing a provider](https://tomas-rampas.github.io/vouchfx-providers/docs/implementing-a-provider.html)** — The complete journey from contract to conformance
- **[Provider hub](https://tomas-rampas.github.io/vouchfx-providers/)** — Registry of all listed providers

## Sample applications

**Four production-grade services to learn from and fork.**

Real microservices in C#, Python, Node.js and Java with complete end-to-end test suites demonstrating vouchfx patterns across multiple providers and technologies. Clone, run one command, see a complete suite execute.

**The samples:**
- **[Orders (C# + ASP.NET)](https://tomas-rampas.github.io/vouchfx-samples/samples/orders-dotnet/README.html)** — REST, PostgreSQL, Kafka, webhooks
- **[Inventory (Python + FastAPI)](https://tomas-rampas.github.io/vouchfx-samples/samples/inventory-python/README.html)** — HTTP, MySQL, RabbitMQ, Redis
- **[Payments (Java + Spring Boot)](https://tomas-rampas.github.io/vouchfx-samples/samples/payments-java/README.html)** — REST, SQL Server, NATS, email
- **[Ledger (Node.js + JSON-RPC)](https://tomas-rampas.github.io/vouchfx-samples/samples/ledger-jsonrpc/README.html)** — Custom community provider (rpc.json-rpc), PostgreSQL, Kafka

**Start here:**
- **[Run a sample](https://tomas-rampas.github.io/vouchfx-samples/docs/RUNNING.html)** — Clone and run any sample in minutes
- **[Custom runner](https://tomas-rampas.github.io/vouchfx-samples/docs/custom-runner.html)** — How the Ledger sample uses a custom runner to consume the Community provider

## The telemetry backend

**Opt-in, privacy-first usage analytics — optional and self-hostable.**

The telemetry system is privacy-first and OFF by default. When enabled, it collects anonymous aggregate counts (tool versions, verdict tallies, which Core step kinds ran, startup timings) — never your test contents, secrets, URLs, or data.

A reference backend implementing the frozen ingest contract is open-source and available for self-hosting.

**Start here:**
- **[Why telemetry?](https://tomas-rampas.github.io/vouchfx-telemetry-backend/docs/why-telemetry.html)** — What is collected, what is never collected, the privacy guarantees
- **[Self-hosting](https://tomas-rampas.github.io/vouchfx-telemetry-backend/docs/self-hosting.html)** — Deploy your own telemetry backend
- **[Verify what would be sent](https://tomas-rampas.github.io/vouchfx-telemetry-backend/docs/why-telemetry.html#verify-exactly-what-would-be-sent-the-local-outbox)** — Inspect your local outbox before any data leaves your machine
- **[Privacy](https://tomas-rampas.github.io/vouchfx-telemetry-backend/docs/privacy.html)** — Data retention, deletion, and consent model

For configuration details and backend availability, see [Telemetry & privacy](https://tomas-rampas.github.io/vouchfx/docs/telemetry.html) in the main engine documentation.

## Where to ask questions

- **Engine, language, or telemetry product questions?** Open an issue on [github.com/tomas-rampas/vouchfx/issues](https://github.com/tomas-rampas/vouchfx/issues) — the main repository.
- **Provider registry, listings, or submissions?** Open an issue on [github.com/tomas-rampas/vouchfx-providers/issues](https://github.com/tomas-rampas/vouchfx-providers/issues).
- **Sample bugs or patterns?** Open an issue on [github.com/tomas-rampas/vouchfx-samples/issues](https://github.com/tomas-rampas/vouchfx-samples/issues).
- **Backend deployment or self-hosting?** Open an issue on [github.com/tomas-rampas/vouchfx-telemetry-backend/issues](https://github.com/tomas-rampas/vouchfx-telemetry-backend/issues).
