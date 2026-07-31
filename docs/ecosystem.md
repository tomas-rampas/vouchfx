# The vouchfx ecosystem

vouchfx is a coordinated ecosystem of five repositories, each with an associated documentation site. This page maps the landscape, so you can find what you need and understand how the pieces fit together.

## Overview

**vouchfx is one engine and four companion repositories.** The engine lives in the main repository and is the declarative YAML platform itself. The companions host reusable providers, production-grade sample applications, an opt-in telemetry backend, and an MCP server for AI-assisted test authoring.

| Repository | What it is | Site | Source |
|---|---|---|---|
| **vouchfx** (main) | The engine: compiler, orchestration, CLI, provider SDK, core providers, and documentation | [https://vouchfx.io/](https://vouchfx.io/) | [github.com/tomas-rampas/vouchfx](https://github.com/tomas-rampas/vouchfx) |
| **vouchfx-providers** | Community provider hub: registry, Vouched badge, conformance testing, examples | [https://providers.vouchfx.io/](https://providers.vouchfx.io/) | [github.com/tomas-rampas/vouchfx-providers](https://github.com/tomas-rampas/vouchfx-providers) |
| **vouchfx-samples** | Four production-grade sample applications with complete test suites in C#, Python, Node.js and Java, plus worked migration examples (Postman, xUnit, SpecFlow) | [https://samples.vouchfx.io/](https://samples.vouchfx.io/) | [github.com/tomas-rampas/vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples) |
| **vouchfx-telemetry-backend** | Opt-in telemetry backend: schema, deployment, verification, self-hosting guide | [https://telemetry.vouchfx.io/](https://telemetry.vouchfx.io/) | [github.com/tomas-rampas/vouchfx-telemetry-backend](https://github.com/tomas-rampas/vouchfx-telemetry-backend) |
| **vouchfx-mcp** | Model Context Protocol (MCP) server for AI-assisted test authoring: schema validation, step-type catalogue lookup, suite execution, and event-stream diagnostics | [https://vouchfx-mcp.vouchfx.io/](https://vouchfx-mcp.vouchfx.io/) | [github.com/tomas-rampas/vouchfx-mcp](https://github.com/tomas-rampas/vouchfx-mcp) |

## The vouchfx engine

**The main repository** — where the platform lives.

The engine comprises the YAML→AST→C#→Roslyn compiler, the Aspire/Testcontainers orchestration layer, the five-layer architecture, twenty-five Core providers across eleven families (HTTP, databases, message publishing and consumption, caches, storage, metrics, traces, mail, webhooks, scripts), the CLI, and the full set of design documentation.

**Start here:**
- **[Getting started](getting-started.md)** — 60-minute path to your first PASS
- **[Recipes](recipes.md)** — Task-oriented, runnable patterns
- **[Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md)** — The five layers, orchestration, memory model, and the frozen provider contract
- **[YAML DSL Specification](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)** — The `.e2e.yaml` grammar and VSCode extension

## The community provider hub

**For when you need a provider the engine doesn't bundle.**

Two governance tiers (Core and Community) plus the maintainer-awarded Vouched badge. Submit your own provider via pull request and have it conformance-tested and listed.

**Start here:**
- **[Consuming a provider](https://providers.vouchfx.io/docs/consuming-a-provider.html)** — How to use a community provider in your suites
- **[Implementing a provider](https://providers.vouchfx.io/docs/implementing-a-provider.html)** — The complete journey from contract to conformance
- **[Provider hub](https://providers.vouchfx.io/)** — Registry of all listed providers

## Sample applications

**Four production-grade services to learn from and fork.**

Real microservices in C#, Python, Node.js and Java with complete end-to-end test suites demonstrating vouchfx patterns across multiple providers and technologies. Clone, run one command, see a complete suite execute.

**The samples:**
- **[Orders (C# + ASP.NET)](https://samples.vouchfx.io/samples/orders-dotnet/README.html)** — REST, PostgreSQL, Kafka, webhooks
- **[Inventory (Python + FastAPI)](https://samples.vouchfx.io/samples/inventory-python/README.html)** — HTTP, MySQL, RabbitMQ, Redis
- **[Payments (Java + Spring Boot)](https://samples.vouchfx.io/samples/payments-java/README.html)** — REST, SQL Server, NATS, email
- **[Ledger (Node.js + JSON-RPC)](https://samples.vouchfx.io/samples/ledger-jsonrpc/README.html)** — Custom community provider (rpc.json-rpc), PostgreSQL, Kafka

**Start here:**
- **[Run a sample](https://samples.vouchfx.io/docs/RUNNING.html)** — Clone and run any sample in minutes
- **[Migrating to vouchfx](https://samples.vouchfx.io/docs/migrating.html)** — Worked examples porting a Postman collection, an xUnit integration test and a SpecFlow feature, each with a field-by-field mapping table
- **[Custom runner](https://samples.vouchfx.io/docs/custom-runner.html)** — How the Ledger sample uses a custom runner to consume the Community provider

## The telemetry backend

**Opt-in, privacy-first usage analytics — optional and self-hostable.**

The telemetry system is privacy-first and OFF by default. When enabled, it collects anonymous aggregate counts (tool versions, verdict tallies, which Core step kinds ran, startup timings) — never your test contents, secrets, URLs, or data.

A reference backend implementing the frozen ingest contract is open-source and available for self-hosting.

**Start here:**
- **[Why telemetry?](https://telemetry.vouchfx.io/docs/why-telemetry.html)** — What is collected, what is never collected, the privacy guarantees
- **[Self-hosting](https://telemetry.vouchfx.io/docs/self-hosting.html)** — Deploy your own telemetry backend
- **[Verify what would be sent](https://telemetry.vouchfx.io/docs/why-telemetry.html#verify-exactly-what-would-be-sent-the-local-outbox)** — Inspect your local outbox before any data leaves your machine
- **[Privacy](https://telemetry.vouchfx.io/docs/privacy.html)** — Data retention, deletion, and consent model

For configuration details and backend availability, see [Telemetry & privacy](telemetry.md) in the main engine documentation.

## The MCP companion

**AI-assisted test authoring — feature-complete for its current scope, documented, not yet on NuGet.**

A local, stdio Model Context Protocol (MCP) server that integrates vouchfx into AI coding agents and other MCP clients (such as Claude Code). It exposes the engine's capabilities programmatically through nine tools and two vendored resources, designed specifically so AI agents can reason about `.e2e.yaml` suites without brittle console-output parsing.

The server provides five offline-capable tools that need no CLI at all: validate a suite against the frozen v1 JSON Schema and collect every structural error; list all registered step types (`family.provider` notation); fetch the full contract for a single step type with all required and optional fields; free-text search the two vendored engine documents (the generated language reference and the recipes library); and diagnose a completed run's event stream — taxonomy-faithful explanations plus evidence-based patch proposals — purely by reading the JSON Lines it already produced, never spawning the CLI or re-running anything. Four further tools invoke the CLI: run a suite and collect its verdict; explain a run in plain language from its event stream; deterministically scaffold a schema-valid `.e2e.yaml` skeleton from structured step/service/dependency specifications, grounded in the live catalogue; and analyse a suite directory against run history and the step catalogue for coverage gaps and history-health signals. The offline tools mean agents can work entirely without the engine CLI when schema validation, documentation browsing, or run diagnosis is enough.

A design choice worth noting: `run_suite` refuses to invoke the CLI if the installed version does not match the pinned engine release (`ENGINE_PIN` file), preventing silent version drift. Only that tool checks version compatibility, since none of the other tools spawn the pinned CLI binary to do their own work. The server also runs suites out-of-process, so a hostile or runaway test cannot hang the server itself. Finally, it passes through vouchfx's already-redacted event streams — it never resolves `${secret:...}` references itself, and never reads or echoes its own process environment into a tool result, progress notification, or resource.

The server is packaged as a C# NuGet dotnet tool (`Vouchfx.Mcp`, command `vouchfx-mcp`) wrapping the published vouchfx CLI at a pinned engine release. It is **not yet published to NuGet**.

**Start here:**
- **[MCP Server documentation](https://vouchfx-mcp.vouchfx.io/)** — Installation, tools and resources, troubleshooting
- **[Overview](https://vouchfx-mcp.vouchfx.io/docs/overview.html)** — What the server wraps, how it is pinned, and its current status
- **[Install & register](https://vouchfx-mcp.vouchfx.io/docs/install.html)** — Set up the server for use with Claude Code or other MCP clients
- **[Tools & resources](https://vouchfx-mcp.vouchfx.io/docs/tools-and-resources.html)** — Reference for all nine tools and two resources
- **[Troubleshooting](https://vouchfx-mcp.vouchfx.io/docs/troubleshooting.html)** — Debugging common issues
- **[GitHub repository](https://github.com/tomas-rampas/vouchfx-mcp)** — Source, development status, and roadmap

## Where to ask questions

- **Engine, language, or telemetry product questions?** Open an issue on [github.com/tomas-rampas/vouchfx/issues](https://github.com/tomas-rampas/vouchfx/issues) — the main repository.
- **Provider registry, listings, or submissions?** Open an issue on [github.com/tomas-rampas/vouchfx-providers/issues](https://github.com/tomas-rampas/vouchfx-providers/issues).
- **Sample bugs or patterns?** Open an issue on [github.com/tomas-rampas/vouchfx-samples/issues](https://github.com/tomas-rampas/vouchfx-samples/issues).
- **Backend deployment or self-hosting?** Open an issue on [github.com/tomas-rampas/vouchfx-telemetry-backend/issues](https://github.com/tomas-rampas/vouchfx-telemetry-backend/issues).
- **MCP server bugs or feature requests?** Open an issue on [github.com/tomas-rampas/vouchfx-mcp/issues](https://github.com/tomas-rampas/vouchfx-mcp/issues).
