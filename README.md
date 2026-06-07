# vouchfx

vouchfx compiles declarative `.e2e.yaml` integration tests into Turing-complete C# (CSX), runs them through Roslyn, and orchestrates the required container topology with .NET Aspire and Testcontainers. It tests distributed .NET systems end-to-end.

## Reserved namespaces

The following namespace prefixes are reserved and enforced at suite start-up (see §5.6 of the Technical Architecture and Engineering Blueprint):

| Prefix | Owner | Purpose |
|---|---|---|
| `Platform.Engine.*` | Engine | Core engine internals — compilation pipeline, orchestration, execution host, verdict taxonomy, reporting event stream. Customer assemblies declaring these namespaces are refused at start-up. |
| `Platform.Steps.*` | Providers | Step providers — e.g. `Platform.Steps.Core.HttpRest`, `Platform.Steps.DbAssert.Postgres`. Each provider lives in its own project under this prefix. Customer assemblies declaring these namespaces are refused at start-up. |
| `Platform.Sdk` | Provider-authoring contract | The public surface through which providers implement `IStepProvider`, `IStepBinder<T>`, `IStepValidator<T>`, `IStepCompiler<T>`, and `IResourceContributor<T>`. This namespace is consumed by providers; it is not part of the engine internals. |

Customer (third-party) assemblies may use any namespace **not** beginning with `Platform.Engine.` or `Platform.Steps.`. Version conflicts between customer assemblies and the engine fail fast at suite start, not at runtime.
