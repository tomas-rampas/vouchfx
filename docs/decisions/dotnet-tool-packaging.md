# Decision record: dotnet tool packaging for the vouchfx CLI

**Status:** Decided  
**Date:** 2026-06-25

## Context

The vouchfx CLI is an Aspire-host executable that carries the `Aspire.AppHost.Sdk` and is marked as an Aspire host (`IsAspireHost: true`). This SDK embeds DCP (Distributed Application Cluster) metadata (`dcpclipath` and `aspiredashboardpath` AssemblyMetadata attributes) at build time, which the CLI requires at runtime to locate and launch the DCP process for orchestrating container topologies. This is a CLAUDE.md hard invariant and is documented in the Architecture Blueprint § 4 and § 19.

The question: can the CLI be packaged as a `dotnet global tool` (via `PackAsTool: true`) given this Aspire dependency, and if so, what are the portability implications?

## Decision

Ship the vouchfx CLI as a `dotnet global tool` (NuGet package with `ToolCommandName: vouchfx`) with the following configuration:

- `PackAsTool: true`
- `ToolCommandName: vouchfx`
- `PackageId: vouchfx`
- `Version: 1.0.0`
- `PackageLicenseExpression: Apache-2.0`
- `IsPublishable: true` (explicitly re-enabled, as the Aspire.AppHost.Sdk sets it false)

This enables developers with a .NET SDK to install the tool via `dotnet tool install -g vouchfx` and execute it without building from source.

## Evidence

Empirical verification on a Windows machine with .NET 8 SDK:

1. **Packing:**  
   `dotnet pack` produces `vouchfx.1.0.0.nupkg` (larger with the added client libraries for new providers), bundling the engine, all twenty-three Core provider DLLs, and Aspire/Testcontainers transitive dependencies.

2. **Installation:**  
   `dotnet tool install --tool-path <dir> --add-source <feed> vouchfx` installs cleanly from the package.

3. **Version query:**  
   Installed tool responds: `vouchfx --version` → `1.0.0+<commit-sha>`.

4. **Help output:**  
   `vouchfx --help` lists the `run` and `telemetry` commands correctly.

5. **End-to-end topology execution:**  
   Running `vouchfx run examples/ci-reference --events <file> --no-decorations` from the installed tool:
   - Started DCP (Aspire AppHost 13.4.2).
   - Brought up the Aspire/Testcontainers topology.
   - Executed the smoke scenario to **verdict PASS, exit code 0**.

The **decisive result:** the embedded DCP path metadata resolves into the per-user NuGet package cache, allowing the installed global tool to start topologies successfully.

## Consequences and Caveats

### DCP Path Resolution Requirement

The embedded DCP path metadata resolves into the per-user NuGet cache at `~/.nuget/packages/aspire.hosting.orchestration.<rid>/…`. On a machine that has previously restored Aspire packages (any developer who has built an Aspire app, or any CI environment that has restored the engine's or the template's dependencies), the DCP binaries are present and the global tool starts topologies correctly.

A truly fresh machine that has **only** the .NET 8 runtime installed and has never resolved the Aspire orchestration packages will **not** have the DCP binaries on the embedded path and the tool will fail to start topologies.

**Frame for users:** the `dotnet global tool` is the primary distribution channel for .NET-SDK-equipped developers and CI pipelines (which already have Aspire/Testcontainers in their dependency graphs). For the zero-prerequisite, self-contained experience (a machine with only the OS and no .NET SDK), use the per-OS native executables produced by the release pipeline (forthcoming).

### Package Size

The nupkg is ~61 MB (framework-dependent; all transitive dependencies bundled). This reflects the full engine and provider surface area.

### Portability vs Convenience Trade-off

- **Gain:** developers can `dotnet tool install -g vouchfx` without cloning or building the repository.
- **Loss:** requires the presence of Aspire orchestration packages in the NuGet cache, which is not guaranteed on a completely fresh system.
- **Mitigation:** document the DCP cache caveat; provide the self-contained native executable as the portable fallback for machines without a .NET SDK or the Aspire workload.

## Related Documents

- CLAUDE.md § "Aspire (§4, §19)" — hard invariant on DCP metadata and `IsPublishable` override.
- Csproj comment (lines 24–34 in `src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj`) — implementation details.
- README.md "Getting started" section — user-facing installation guidance.
