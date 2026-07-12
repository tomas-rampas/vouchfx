# KB: "The Aspire orchestration component is not installed" — DCP path portability in packaged distributions

**Article type:** knowledge base / incident write-up
**Applies to:** the `vouchfx` dotnet global tool, NuGet.org releases **1.0.0-alpha.5 and earlier** (and the self-contained archives built from the same commits)
**Status:** fixed in **1.0.0-alpha.6** (2026-07-12, PR [#206](https://github.com/tomas-rampas/vouchfx/pull/206))
**Severity:** complete failure of the packaged-tool distribution channel — every affected install failed its first `vouchfx run` on every machine other than the CI runner that built the package

## Symptom

On a freshly installed tool (`dotnet tool install --global vouchfx --prerelease`), the first `vouchfx run` fails before any scenario executes:

```
fail: Microsoft.Extensions.Hosting.Internal.Host[11]
      Hosting failed to start
      System.IO.FileNotFoundException: The Aspire orchestration component is not installed
      at "/home/runner/.nuget/packages/aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp".
      The application cannot be run without it.
```

Two details make the message diagnostic:

- the path begins `/home/runner/…` — a GitHub Actions runner's home directory, not a path that exists on any user machine; and
- the package RID is `linux-x64` even when the failing machine is Windows or macOS.

The run reports an **Environment error** (infrastructure), never a test verdict, and — by the engine's four-verdict contract — exits `0` unless `--fail-on-env-error` is passed. Populating your own NuGet cache does **not** help on affected versions: the tool never looks there.

## Root cause

vouchfx orchestrates container topologies through .NET Aspire's DCP (Developer Control Plane). DCP's binaries are not part of Aspire's ordinary NuGet dependency graph; they ship in a platform-specific package, `aspire.hosting.orchestration.<rid>`, and Aspire locates them through metadata that the `Aspire.AppHost.Sdk` **bakes into the host assembly at build time**:

- `DcpCliPath` — the **absolute** path of the `dcp` executable inside the **build machine's** NuGet cache, for the **build machine's** RID;
- `DcpExtensionsPath`, `aspiredashboardpath` — companions carrying the same build-machine-baked absolute paths.

At run time, Aspire 13.4.2 resolves the DCP location in this precedence order (upstream `Aspire.Hosting/Dcp/DcpOptions.cs`):

1. the `DcpPublisher:CliPath` configuration key;
2. the `ASPIRE_DCP_PATH` environment variable;
3. the baked assembly metadata.

For a locally built binary this is invisible: the baked path points into the same machine's cache and simply works. For a **packaged** binary the baked path travels with the package. The NuGet.org tool is packed on `ubuntu-latest`, so every user received a tool that would only ever look at `/home/runner/.nuget/packages/aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp` — a path (and platform) that cannot exist on their machine. No amount of cache population, restoring, or reinstalling could fix it, because nothing the user does changes the baked metadata.

### Why the release pipeline did not catch it

The packaging decision was validated empirically — pack, install, run, PASS — but **on the same machine that built the package**, where the baked path coincidentally resolved. The same holds on CI: a smoke test running on the runner that packed the tool shares that runner's cache path, so it stays green while every real user is broken. The evidence was true; it just did not generalise across machines.

## Resolution

The engine now self-heals at topology start (`HeadlessTopology.StartAsync`, the single chokepoint through which every topology — CLI, test harnesses, custom runners — passes). A pure decision component, `DcpPathResolver`, is consulted before Aspire builds the application:

| Condition | Action |
|---|---|
| `ASPIRE_DCP_PATH` is set | Do nothing — Aspire's own resolution honours it (priority 2), and writing configuration would silently outrank the user's explicit choice. |
| Baked metadata path exists on disk | Do nothing — local builds behave exactly as before. |
| Baked path is dead, and the executing machine's NuGet cache (`NUGET_PACKAGES`, else `~/.nuget/packages`) contains `aspire.hosting.orchestration.<portable-rid>` at the engine's pinned Aspire version | Inject that path via the `DcpPublisher:CliPath` configuration key (priority 1; `ExtensionsPath` derives automatically). |
| Nothing resolvable | Fail with an actionable **Environment error** naming the exact missing package, the probed path, and the remedy. |

The RID is normalised to the portable form (`win-x64`, `linux-x64`, `osx-arm64`, …) from the machine's actual OS and architecture, so distro-qualified runtime identifiers such as `ubuntu.22.04-x64` cannot derail the probe.

### If you are on an affected version

- **Upgrade** to 1.0.0-alpha.6 or later: `dotnet tool update --global vouchfx --prerelease`; or
- **build from source** ([getting started guide](../getting-started.md)) — locally built binaries have never been affected; or
- set **`ASPIRE_DCP_PATH`** to the directory containing a `dcp` executable if you have one (for example `~/.nuget/packages/aspire.hosting.orchestration.<rid>/13.4.2/tools/`) — honoured by Aspire itself, even on affected versions.

On fixed versions the only prerequisite is that the orchestration package exists in your cache; restoring this repository (`dotnet restore vouchfx.sln`) puts it there. Note that `dotnet workload install aspire` is **not** a remedy on any version: the Aspire workload was retired with Aspire 9, installs Aspire 8.2.x packs, and never touches the NuGet cache. See [troubleshooting](../troubleshooting.md#the-aspire-orchestration-component-is-not-installed-could-not-be-located).

## How regression is prevented

- **Unit suite** — the `DcpPathResolver` decision table is pinned branch-by-branch by OS-agnostic unit tests that run in every PR build on both Windows and Linux, including the `ASPIRE_DCP_PATH` short-circuit, RID normalisation, quote/separator handling of `NUGET_PACKAGES`, version derivation, and a proof that the unresolvable failure classifies as an Environment error (never a test failure).
- **Cross-machine release gate** — a `smoke-test-packaged-tool` job in the release pipeline installs the freshly packed nupkg, restores the orchestration package into a *second* cache location, **moves the runner's own `~/.nuget/packages` away** (killing the same-machine coincidence that hid the bug), and runs a real scenario through the installed tool. The job gates `publish-release`, so a portability regression cannot reach NuGet.org.
- **The gate can genuinely fail** — the smoke run passes `--fail-on-env-error --fail-on-inconclusive` *and* asserts the JUnit output records a real pass. Without those, a regressed tool would report an Environment error and exit `0` — only `Fail` breaks CI by default — and the gate would have been green for exactly the failure class it exists to catch.

## Timeline

| Date | Event |
|---|---|
| 2026-06-25 | dotnet-tool packaging decided; validated same-machine (see the [decision record](../decisions/dotnet-tool-packaging.md)) |
| 2026-07-08 → 07-11 | 1.0.0-alpha.1 … alpha.5 published to NuGet.org — all carrying the baked runner path |
| 2026-07-12 | Bug found while executing every step of the published getting-started guide on a clean path; reproduced deterministically; engine self-heal, release gate, and documentation truth-up merged (PR #206) |
| 2026-07-12 | **1.0.0-alpha.6** released with the fix; the new cross-machine smoke gate ran and passed on that release pipeline |

## References

- [Decision record: dotnet tool packaging](../decisions/dotnet-tool-packaging.md) — including the addendum on why the original evidence did not hold cross-machine
- [Troubleshooting: the Aspire orchestration component is not installed](../troubleshooting.md#the-aspire-orchestration-component-is-not-installed-could-not-be-located)
- [Getting started — the Aspire orchestration prerequisite](../getting-started.md#installing-vouchfx)
- Upstream Aspire 13.4.2 `DcpOptions.cs` — the `DcpPublisher:CliPath` / `ASPIRE_DCP_PATH` / assembly-metadata precedence
