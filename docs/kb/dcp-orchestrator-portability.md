# KB: DCP portability in installed distributions

**Applies to:** CLI global tools, archives and installers.

**Updated:** 2026-10-13. Core `1.0.0` introduced bundled DCP; legacy `1.0.0-rc.6` remains cache-dependent.

## Symptoms and cause

A legacy cache-based or incomplete installation can report `The Aspire orchestration component is not installed` or
`could not be located`, often naming a packaging runner's NuGet directory. Aspire.AppHost.Sdk
embeds an absolute build-machine path; that path and its RID are not portable. Topology never
starts and no test steps execute. This is an **EnvironmentError**, not a failed product assertion.

The original same-machine packaging test passed because its build cache was present. The July
2026 fallback resolved matching tools from a different user's cache, but the release smoke itself
restored an AppHost into that cache. It proved relocation, not a fresh installation. A clean
`1.0.0-rc.6` installation on 2026-10-10 reproduced the missing tools; the same candidate started
with matching Aspire 13.4.2 tools supplied through `ASPIRE_DCP_PATH`.

## Behaviour introduced in Core 1.0.0

The published `1.0.0` global tool carries complete `win-x64`, glibc `linux-x64`, `osx-x64` and `osx-arm64` DCP trees.
Archives/installers carry their selected RID. Paths are relative to the resolved host assembly,
not the current directory: `dcp/13.4.2/<rid>/tools/`.

| Condition | Behaviour |
|---|---|
| Explicit `ASPIRE_DCP_PATH` | Aspire honours that directory; bundle/cache probing cannot override it. |
| Installed versioned bundle | Select the current process RID before stale metadata or a warmed cache. |
| Declared bundle lacks DCP/tunnel helper | Actionable provisioning error; reinstall the complete distribution. |
| Unsupported bundled RID | Report supported RIDs and the explicit-override option. |
| Source/custom AppHost without a bundle | Preserve existing metadata and exact-version local-cache fallback. |

Installed distributions also relocate Aspire's AppHost directory to the host assembly's directory
and allocate a separate writable store under the system temporary directory for each topology.
The store is removed after a failed build/start or normal teardown. Embedded build-machine
project and intermediate-output paths never select the installed tool's runtime storage; the
installation directory may be read-only. Source/custom AppHosts without a distribution manifest retain Aspire's existing paths.

On Unix, archives and installers preserve native execute bits. Global-tool startup repairs them
in the selected bundle only; read-only installs with broken permissions require their owner to
repair them. DCP resolution remains inside inline Aspire startup for future environment adapters.

## Remedy for published legacy packages

Upgrade to the latest stable bundled CLI with
`dotnet tool update --global vouchfx`. Its installed path requires no source checkout,
Aspire workload or warmed NuGet cache.

If you must retain a legacy package, supply exact platform tools through `ASPIRE_DCP_PATH`.
It names the directory containing `dcp`/`dcp.exe`, not the executable. An AppHost restore at
Aspire 13.4.2 can populate those tools for legacy/source builds; the retired Aspire workload cannot.
Do not describe an rc.6 install with this override as a standalone pass.
See [troubleshooting](../troubleshooting.md#the-aspire-orchestration-component-is-not-installed-could-not-be-located).

## Regression gates

Resolver tests cover bundle priority, all advertised RIDs, unsupported/incomplete bundles and
explicit override. Glue tests verify host-relative configuration and Unix permission repair with
an empty cache. Real Aspire-store tests cover stale Windows/Unix metadata, isolated concurrent
stores, installation-file preservation and failure/disposal cleanup. Packaging checks compare complete native trees against restored exact-version
NuGet payloads, validate executable hashes/platforms and enforce a 250,000,000-byte tool ceiling.

Release preflights launch tool/archive binaries on all four RIDs and verify installer payloads.
The installed-tool Linux Docker smoke restores no source project, hides the default cache, runs
outside the checkout with an empty alternate cache, requires genuine passing JUnit scenarios and
rejects new container/network residue. These gates block publication; a preflight alone is not
live orchestration evidence. See the [packaging decision](../decisions/dotnet-tool-packaging.md).
