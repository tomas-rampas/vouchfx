# Vouchfx.Engine.Planning

The vouchfx **Planner**: a deterministic, read-only coverage-and-gap analysis over three
engine-side sources — the **declared** universe (an `.e2e.yaml` suite folder), the
**exercised** reality (a JSON Lines event history), and the **available** vocabulary (the
live Spec A step catalogue) — emitting a schema-versioned coverage-and-gap report.

The Planner never writes a suite file, never calls a model API, and never claims coverage
it cannot evidence from its declared inputs.

## Entry points

```csharp
using Vouchfx.Engine.Planning;

var registry = StepKindRegistry.BuildAndFreeze(coreProviderAssemblies);
var request = new PlanRequest(SuitePath: "suites/", EventsPath: "events/");

PlanReportDocument report = PlanExport.BuildPlan(request, registry, engineVersion: "1.0.0");
string json = PlanExport.SerializePlan(report);
```

`PlanExport.BuildPlan` is the sibling, in spirit, of
`Vouchfx.Engine.Compilation.Schema.EngineExport.BuildCatalogue`: a static export pair over a
frozen `StepKindRegistry`, usable in-process by the CLI, an MCP host, or any other .NET
tool without shelling out or reflecting over CLI internals.

## What it reads — and does not

- Reads: the suite folder (`SuiteSetLoader`), an optional event history file or directory
  (`EventHistoryReader`), and the frozen `StepKindRegistry` passed by the caller.
- Never reads: production traffic, telemetry, OpenAPI/AsyncAPI/docker-compose files, or git
  history.
- Never resolves secrets, never echoes an observation payload or a captured value into the
  report (EDGE-006).

## Report shape

`PlanReportDocument` (see `Report/PlanReportDocument.cs`, `Report/PlanFinding.cs`,
`Report/PlanFindingKinds.cs`) is frozen at v1 by a golden-file CI gate
(`PlanReportContractFreezeTests`) in the same style as `SchemaFreezeTests` /
`SdkContractFreezeTests` / `EventContractFreezeTests`. Evolution within v1 is additive only.

## Status

Packable (`IsPackable=true`) but not yet part of the release pack-and-publish loop — a new
NuGet package ID needs an owner pre-flight on nuget.org before first publication. The public
entry point is proven by an in-solution unit test in the meantime.
