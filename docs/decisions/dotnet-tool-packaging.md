# Decision record: dotnet tool packaging for the vouchfx CLI

**Status:** Decided — amended 2026-07-12 (see Addendum)  
**Date:** 2026-06-25

## Context

The vouchfx CLI is an Aspire-host executable that carries the `Aspire.AppHost.Sdk` and is marked as an Aspire host (`IsAspireHost: true`). This SDK embeds DCP (Developer Control Plane) metadata (`dcpclipath` and `aspiredashboardpath` AssemblyMetadata attributes) at build time, which the CLI requires at runtime to locate and launch the DCP process for orchestrating container topologies. This dependency is documented in the Architecture Blueprint § 4 and § 19.

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
   `dotnet pack` produces `vouchfx.1.0.0.nupkg` (larger with the added client libraries for new providers), bundling the engine, all twenty-five Core provider DLLs, and Aspire/Testcontainers transitive dependencies.

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

**Frame for users:** the `dotnet global tool` is the primary distribution channel for .NET-SDK-equipped developers and CI pipelines (which already have Aspire/Testcontainers in their dependency graphs). For the zero-prerequisite, self-contained experience (a machine with only the OS and no .NET SDK), use the per-OS native executables produced by the release pipeline (attached to each GitHub release: archives and installers, cosign-signed).

### Package Size

The nupkg is ~61 MB (framework-dependent; all transitive dependencies bundled). This reflects the full engine and provider surface area.

### Portability vs Convenience Trade-off

- **Gain:** developers can `dotnet tool install -g vouchfx` without cloning or building the repository.
- **Loss:** requires the presence of Aspire orchestration packages in the NuGet cache, which is not guaranteed on a completely fresh system.
- **Mitigation:** document the DCP cache caveat; provide the self-contained native executable as the portable fallback for machines without a .NET SDK.

## Addendum (2026-07-12): the "decisive result" above did not hold cross-machine

The empirical pass recorded under Evidence was a **same-machine** result: the tool was packed and executed on the same Windows machine, so the absolute DCP path the Aspire.AppHost.Sdk baked into `AssemblyMetadata` (`dcpclipath`) happened to exist. The published NuGet.org packages are packed on `ubuntu-latest`, so they carry `/home/runner/.nuget/packages/aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp` — a path (and RID) that cannot exist on any user machine. Every install of `vouchfx` 1.0.0-alpha.5 and earlier from NuGet.org therefore failed its first `vouchfx run` with `FileNotFoundException: The Aspire orchestration component is not installed at "/home/runner/..."`, regardless of the user's NuGet cache contents. Reproduced deterministically by packing against an alternate `NUGET_PACKAGES` root and hiding it before execution.

**Resolution:** the engine now self-heals at topology start. `DcpPathResolver` (in `Vouchfx.Engine.Orchestration`) checks the baked `DcpCliPath` metadata; when the path does not exist it re-resolves `aspire.hosting.orchestration.<executing-machine-RID>/<pinned Aspire version>/tools/dcp` from the executing machine's NuGet cache (`NUGET_PACKAGES`, else `~/.nuget/packages`) and injects it via the `DcpPublisher:CliPath` configuration key, which Aspire 13.4.2 gives precedence over assembly metadata (`ConfigureDefaultDcpOptions` in upstream `DcpOptions.cs`; `ExtensionsPath` is auto-derived). If the cache lacks the package, topology start fails with an actionable environment error naming the exact package, version, probed path, and remedy. The `smoke-test-packaged-tool` job in `release.yml` now simulates the cross-machine hand-off (dead baked path, populated user cache) and gates `publish-release`. A mention of the retired Aspire *workload* as a fallback has been removed throughout: the workload was discontinued with Aspire 9 and installs Aspire 8.2.x packs to the SDK packs directory, so it can never satisfy the version-exact NuGet-cache requirement.

## Related Documents

- Architecture Blueprint § 4 and § 19 — Aspire orchestration and technology choices.
- `src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj` — implementation details of tool packaging.
- README.md "Getting started" section — user-facing installation guidance.
