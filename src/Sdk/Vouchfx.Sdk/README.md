# Vouchfx.Sdk

The compile-time **provider SDK** for authoring [vouchfx](https://github.com/tomas-rampas/vouchfx)
step providers. This package is the **frozen v1 contract** (`v1.x` engine series) — the
narrow, strongly-typed surface a provider compiles against.

**Install this package if you are writing a new vouchfx step provider.** If you only need
to *author and run* `.e2e.yaml` suites, install the [`vouchfx`](https://www.nuget.org/packages/vouchfx)
CLI tool instead — `Vouchfx.Sdk` is for provider authors, not test authors.

vouchfx compiles declarative `.e2e.yaml` integration tests into C#, runs them through
Roslyn, and orchestrates the required container topology with .NET Aspire + Testcontainers
to test **distributed .NET systems end-to-end** — one business transaction crossing a REST
call, a Kafka event, a DB mutation and an outbound webhook.

## What this package gives you

A step provider is a **compile-time, source-level plugin** — there is no runtime loader, no
dynamic assembly loading, and no sandbox. You add a project, reference `Vouchfx.Sdk`,
implement the contract interfaces, and mark your provider class `[StepProvider]`; the
engine's reflective `StepKindRegistry` (frozen at startup) discovers it.

The contract surface exposed by this package:

| Type | Role |
|------|------|
| `IStepProvider` | Provider identity (`Kind` + `Metadata`). |
| `IStepBinder<TModel>` | Deserialises a step's YAML into a strongly-typed model + supplies its JSON Schema fragment. |
| `IStepValidator<TModel>` | Validates a bound model with author-friendly diagnostics. |
| `IStepCompiler<TModel>` | Emits the `CsxFragment` spliced into the compiled test delegate. |
| `IResourceContributor<TModel>` | Contributes Aspire resources a step needs. |
| `IStepModel` | Marker every provider step-model record implements. |
| `StepKindId` | The `<family>.<provider>` identity (e.g. `db-assert.postgres`). |
| `ProviderMetadata` | Version / min-engine-version / licence / authors. |
| `CsxFragment` | `RequiredUsings` + `RequiredHelpers` + one brace-enclosed `StatementBlock`. |
| `[StepProvider]` | Discovery attribute placed on the provider class. |

## Quick start

```csharp
using Vouchfx.Sdk;

[StepProvider]
public sealed class MyProvider : IStepProvider
{
    public StepKindId Kind => new("db-assert", "postgres");

    public ProviderMetadata Metadata => new(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "you" });
}
```

Then implement `IStepBinder<TModel>`, `IStepValidator<TModel>`, `IStepCompiler<TModel>`,
and (if the step needs infrastructure) `IResourceContributor<TModel>` for your model.

## Authoring rules that matter

- **Step type = `<family>.<provider>`** — family is the intent (`db-assert`), provider is the
  technology (`postgres`).
- **Models are strongly-typed records**, never `Dictionary<string,object>`.
- **`CsxFragment` composition:** `RequiredUsings` are bare namespace strings (never inline
  `using` lines); `RequiredHelpers` are nested static classes prefixed with your provider id;
  the `StatementBlock` is exactly one brace-enclosed C# block. `using var` is illegal in a
  Roslyn script body — use plain `var` + explicit `.Dispose()` in a `finally`. Sanitise step
  ids with `CsxFragment.SanitiseId` before splicing.
- Cross-step state passes **only** through the `Vars` global.

The **v1 interface contract is frozen for the v1.x engine series** — evolution is additive
only, through compatible optional capabilities and separately named, immutable helper generations.

## Versioned Kafka security helper

The current source adds `KafkaSecurityV2Helper.Source` as an explicit opt-in for custom Kafka
providers. Core Kafka providers use this same canonical value. To adopt it, put the constant in
`CsxFragment.RequiredHelpers` and update the emitted call together:

```csharp
new CsxFragment(
    Array.Empty<string>(),
    new[] { KafkaSecurityV2Helper.Source },
    "{ KafkaSecurity_V2_Helpers.ConfigureClient(Security, \"broker\", new Confluent.Kafka.ProducerConfig()); }");
```

A provider must also implement `ICompileReferenceContributor` and contribute the
`Confluent.Kafka` assembly. Substitute the provider's runtime target/configuration in the emitted
call; never bake certificate paths or passphrases into the script. V2 preserves TLS/mTLS selection
and diagnoses a passphrase-only `tls` declaration from an embedder bypassing schema validation,
after checking contradictory client identity. Normal schema validation already forbids that field.

Existing providers need no rebuild to compose with V2. The legacy `KafkaSecurityHelper.Source`
and its `KafkaSecurity_Helpers` identity remain frozen, including their older diagnostic behaviour.
Upgrading the SDK or rebuilding unchanged source does not opt a provider in: change the helper
and call identity, then rebuild only that provider. V2 is also immutable; future behaviour changes
require a new emitted identity. Identical V2 copies deduplicate across Core and custom providers.
The constant is inlined, so this opt-in adds no runtime SDK API call; set `MinEngineVersion` from
the runtime APIs actually used, rather than automatically requiring the SDK's release version.

## Status

Stable `Vouchfx.Sdk` packages are available on NuGet.org. To use the `1.0.0` provider-contract baseline, install with `dotnet add package Vouchfx.Sdk --version 1.0.0`. The frozen v1 interfaces are stable for the whole `v1.x` series.

## Contributing a provider

The authoritative provider-authoring guide is the **provider architecture** section (§13) of
the Technical Architecture and Engineering Blueprint, alongside the `CONTRIBUTING` guide in
the repository:

- Provider architecture (§13): [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](https://github.com/tomas-rampas/vouchfx/blob/main/docs/01_Technical_Architecture_and_Engineering_Blueprint.md)
- Contributing guide: [`CONTRIBUTING.md`](https://github.com/tomas-rampas/vouchfx/blob/main/CONTRIBUTING.md)

## Licence

Apache-2.0. See [`LICENSE`](https://github.com/tomas-rampas/vouchfx/blob/main/LICENSE).
