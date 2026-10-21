# Decision: trusted providers loaded at CLI startup

The CLI admits external provider binaries into the default assembly load context, then freezes a merged registry before discovering suites. This extends distribution of source-level compilation plugins: providers still bind typed models and emit C#, rather than executing steps through a separate plugin host.

The default context preserves SDK type identity and makes provider-contributed compile references available to both ordinary compilation and an isolated compile worker. Provider dependencies must resolve for schema contribution, generated scripts and diagnostics for the entire process lifetime. Collectible contexts remain reserved for generated scripts and their opted-in satellite assemblies.

All configured directories are metadata-scanned before external assemblies are loaded. Host-owned copies are pruned, reserved identities and namespaces are refused, and private assembly conflicts fail deterministically. SDK v1 and shared engine abstractions use the host's frozen v1 API; minimum engine capability is independently enforced from provider metadata. Framework roll-forward follows the host's canonical framework assemblies.

A single immutable invocation snapshot feeds discovery, schema, catalogue, validation, planning and execution. Bundled assembly membership alone determines the Core catalogue tier. Duplicate kinds fail instead of using directory priority. Watch sessions retain the admitted snapshot and require a process restart to change binaries.

This is trusted-code loading, not a security boundary. The unreleased package acquisition commands stage outside the provider scan root and reuse metadata admission without loading provider code. They use NuGet.Protocol 7.9.0 for one explicitly selected source, record hashes, and do not verify package signatures. Signature policy and new managed dependency types remain separate work. The NuGet client APIs follow the [official SDK guidance](https://learn.microsoft.com/en-us/nuget/reference/nuget-client-sdk). See [External step providers](../external-providers.md) for the directory contract and operational limitations.
