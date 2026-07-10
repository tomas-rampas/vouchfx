# Vouchfx.Engine.Abstractions

Core value types for the [vouchfx](https://github.com/tomas-rampas/vouchfx) engine: the
four-outcome **verdict taxonomy** (`Verdict`: Pass / Fail / Environment error /
Inconclusive), `StepOutcome`, `VerifyMode`, secret/webhook/trace capture contracts, and
`ScriptGlobalVariables` — the single typed surface a compiled `.e2e.yaml` delegate touches
the environment through.

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
