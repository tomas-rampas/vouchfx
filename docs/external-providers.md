# External step providers

The CLI can load trusted provider assemblies at startup without rebuilding the engine. This capability is unreleased. Providers use the frozen `Vouchfx.Sdk` v1 contract and contribute schema, binding, validation and generated C# exactly as bundled providers do.

## Prepare a directory

Build the provider for .NET 8, or obtain its DLL from a published package you trust. Extracting a NuGet package does not install its dependencies: supply the provider's managed dependency closure alongside its DLL, omitting assemblies already supplied by the CLI. There is no package download command yet.

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
