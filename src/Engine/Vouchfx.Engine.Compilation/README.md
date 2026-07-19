# Vouchfx.Engine.Compilation

The [vouchfx](https://github.com/tomas-rampas/vouchfx) **compile-once pipeline**: the
JSON Schema `DocumentValidator` (JsonSchema.Net, composed from every registered provider's
schema fragment), `CsxAssembler` (splices provider `CsxFragment`s into one script), and
`RoslynScriptCompiler` — emit once to a `MemoryStream`, load into a collectible
`AssemblyLoadContext`, run N times, unload. Never `CSharpScript.EvaluateAsync()`.

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

- Documentation: <https://vouchfx.io/>
- Community provider hub: <https://github.com/tomas-rampas/vouchfx-providers>

Apache-2.0.
