# External step providers

The CLI can load trusted provider assemblies at startup without rebuilding the engine. This capability is prepared for Core 1.2.0 and remains unreleased until its GitHub Release is published. Providers use the frozen `Vouchfx.Sdk` v1 contract and contribute schema, binding, validation and generated C# exactly as bundled providers do.

## Install from a package

Package acquisition is also unreleased. Install a published provider by exact NuGet package ID or by its Community registry step kind:

```bash
vouchfx providers install rpc.json-rpc
vouchfx providers install Vouchfx.Community.JsonRpc --version 1.0.0
vouchfx providers list
vouchfx providers list --offline --json
vouchfx providers uninstall Vouchfx.Community.JsonRpc
```

Installation defaults to `~/.vouchfx/providers/<lowercase-package-id>/`. Re-running `install` replaces that package directory; uninstall uses the NuGet package ID. Arguments shaped like a DSL step kind consult the HTTPS Community registry first, so a package cannot shadow a registered alias. Other arguments are exact NuGet IDs, with registry suggestions when absent. Use `--package-id` to force a literal package ID without contacting the registry, including an offline ID that resembles a step kind. Ambiguous and source-only registry entries require an explicit published package ID. The root package defaults to its latest listed stable version; `--version` pins an exact version, including a prerelease. Dependencies use a consistent version graph satisfying all selected ranges and compatible .NET 8 `lib` assets. Packages requiring separate RID-specific native or runtime assets are outside this installer contract.

Use `--source <https-v3-index>` to select another feed. Only that source is consulted for packages and dependencies. HTTP, redirects, embedded URI credentials and query strings are refused; configure the final HTTPS endpoint and matching credentials through NuGet configuration. An explicit local feed directory is supported. `--directory <root>` overrides the installation root for install, uninstall and list; list then ignores environment and home provider paths. For example, a hermetic local-feed workflow is:

```bash
vouchfx providers install Acme.Provider --package-id --source ./local-feed --directory ./owned-providers
vouchfx run ./tests --providers ./owned-providers --no-default-providers
```

Also unset `VOUCHFX_PROVIDERS` for the hermetic run. Without `--directory`, `providers list` inspects configured environment and home directories. If the public registry is unavailable, installed results remain available with an offline note. `--offline` avoids that request entirely.

Installation checks downloaded package IDs and versions, selects compatible assets, prunes compatible host-owned assemblies and refuses private dependency conflicts. It records SHA-256 hashes of downloaded packages and installed DLLs in `provider-install.json`, retaining package licence/notice metadata under `_metadata`. These records support inspection; package signatures are not verified. Community packages remain unendorsed trusted code. Install-time checks use metadata without executing providers; normal startup independently enforces provider minimum engine versions, complete type loading, schema contribution and duplicate step kinds.

Updates stage and validate outside the provider scan root, then replace the active directory. Failure before promotion restores the previous version. Interrupted backup moves recover on the next mutating provider command. Installer-managed roots coordinate startup scans with an existing lock; a scan during an update fails with a retry diagnostic. Reading unmanaged provider directories creates no management files. Locked cleanup files may remain quarantined outside the scan root until a later command can remove them. Restart watch sessions after any provider installation, update or removal.

Resource limits are 64 MiB per download/archive entry, 256 MiB expanded per archive, 512 MiB per downloaded or installed closure, 4,096 archive entries or installed files, a 1 MiB manifest, 128 packages and 1,024 resolution attempts. Unsafe archive paths, symlinks/reparse points, conflicting file names and linked installation paths are refused.

## Prepare a directory

Build the provider for .NET 8, or obtain its DLL from a published package you trust. Extracting a NuGet package does not install its dependencies: supply the provider's managed dependency closure alongside its DLL, omitting assemblies already supplied by the CLI. Manual preparation remains available for source builds and distributions outside the acquisition contract.

Use one immediate subdirectory per provider:

```text
providers/
  jsonrpc/
    Vouchfx.Community.JsonRpc.dll
  another-provider/
    Acme.Provider.dll
    Acme.PrivateDependency.dll
```

Managed assemblies directly in the search directory are also supported. Deeper nested directories are not scanned. Native DLLs and files without managed metadata are ignored; a malformed managed image or unreadable file refuses startup.

For the published `Vouchfx.Community.JsonRpc` 1.0.0 package, copy `lib/net8.0/Vouchfx.Community.JsonRpc.dll` into `providers/jsonrpc/`. Its JSONPath dependency is already supplied by the current CLI; do not copy older engine or SDK binaries from a custom runner's build output.

```bash
vouchfx list --providers ./providers
vouchfx schema --providers ./providers --output composed-schema.json
vouchfx validate ./tests --providers ./providers
vouchfx run ./tests --providers ./providers
vouchfx run ./tests --providers ./providers --isolate-compile
```

`run` (including watch and parallel runs), `validate`, `list`, `schema`, `scaffold` and `plan` use the same merged provider set. External entries remain Community-tier catalogue entries. Providers can target existing managed dependency types; adding new managed dependency types is not supported by this loader.

## Search paths and repeatability

Search directories are considered in this order:

1. Repeated `--providers <directory>` options, in argument order.
2. `VOUCHFX_PROVIDERS`, a path list separated by `;` on Windows or `:` on Unix.
3. `~/.vouchfx/providers`, when it exists.

Duplicate paths are normalised and scanned once. Missing explicit or environment paths are errors. A missing implicit home directory is ignored. `--no-default-providers` suppresses the home directory only. For a hermetic invocation, also unset `VOUCHFX_PROVIDERS` and supply only the intended directories.

Search order is not a shadowing mechanism. Duplicate step kinds always fail, including conflicts with bundled providers; diagnostics identify the originating assemblies.

## Compatibility and trust

Providers execute trusted code in the default assembly load context. Directory loading is not a sandbox. Installing a Community provider does not imply maintainer endorsement. Only use assemblies and dependencies you trust.

Before loading external code, the CLI inspects managed metadata, refuses engine/provider namespace impersonation (`Vouchfx.Engine.*`, `Vouchfx.Steps.*`), checks dependency identities, and requires SDK v1. Provider `MinEngineVersion` is checked before schema contribution. Frozen SDK v1 references and the engine's shared abstractions bind to the host's v1 assemblies; framework references bind to the host's compatible framework assembly. Private dependencies must have matching identities, and conflicting copies are refused rather than silently selected.

Providers and their dependencies remain loaded for the process lifetime. Restart watch mode after changing provider binaries. Generated scripts retain their collectible execution contexts; `--isolate-compile` moves compilation into a worker and does not isolate provider code or script execution.

| Diagnostic | Action |
|---|---|
| Provider directory does not exist | Correct the explicit path or environment variable. |
| Missing dependency | Place the matching managed dependency beside the provider DLL. |
| Provider assembly conflict | Remove incompatible copies; rebuild against the CLI's shared dependencies where necessary. |
| Unsupported SDK | Use a provider built against SDK v1. |
| Requires engine version | Upgrade the engine to the provider's declared minimum. |
| Reserved namespace or host identity | Obtain a provider using its own namespace and assembly name. |
| Duplicate step kind | Remove one of the competing provider assemblies. |

Provider authors should use the [implementation guide](https://providers.vouchfx.io/docs/implementing-a-provider.html). The [provider registry](https://providers.vouchfx.io/registry/) lists Community submissions.
