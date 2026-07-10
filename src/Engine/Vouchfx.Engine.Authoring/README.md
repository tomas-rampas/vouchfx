# Vouchfx.Engine.Authoring

The [vouchfx](https://github.com/tomas-rampas/vouchfx) `.e2e.yaml` **front end**: the
`YamlDocumentParser` (YamlDotNet-backed document model — `E2eDocument`,
`EnvironmentSpec`, `MetadataSpec`, `SeedSpec`, `StepSpec`) and the `AstBuilder` that turns
a parsed document into the `ScenarioAst` / `StepNode` tree consumed by the compiler.

## You probably don't want this package directly

This package is published **only** so that
[`Vouchfx.Sdk.Testing`](https://www.nuget.org/packages/Vouchfx.Sdk.Testing)'s dependency
graph resolves from NuGet. It is **versioned, not frozen** — it evolves at the engine's
release cadence, unlike the provider contract.

- Writing a step provider? Reference
  **[`Vouchfx.Sdk`](https://www.nuget.org/packages/Vouchfx.Sdk)** — the frozen v1 contract.
- Testing a step provider? Reference
  **[`Vouchfx.Sdk.Testing`](https://www.nuget.org/packages/Vouchfx.Sdk.Testing)**.

## Learn more

- Documentation: <https://tomas-rampas.github.io/vouchfx/>
- Community provider hub: <https://github.com/tomas-rampas/vouchfx-providers>

Apache-2.0.
