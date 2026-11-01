# Decision record: portable CLI and DCP distribution

**Status:** published in stable Core `1.0.0`; legacy `1.0.0-rc.6` remains cache-dependent.

**Updated:** 2026-10-25. Original tool decision: 2026-06-25.

## Problem and evidence

Aspire 13.4.2 embeds absolute build-cache DCP paths in AppHost metadata. A tool installed on a
fresh machine cannot use those paths. The original same-machine packaging smoke was insufficient:
its restored cache concealed the missing payload. The 2026-07-12 cache fallback improved relocated
cache resolution but still required a matching Aspire restore. A clean `1.0.0-rc.6` installation
measured on 2026-10-10 still failed before Docker; supplying matching tools through `ASPIRE_DCP_PATH`
allowed topology execution. Neither result proves the published tool works standalone.

## Decision

Keep the framework-dependent `net8.0` global tool (`PackAsTool`, `ToolCommandName: vouchfx`) and
self-contained multi-file archives/installers. Do not use single-file publishing: provider discovery
needs `Assembly.Location`.

Current source enables major runtime roll-forward, so the same `net8.0` tool runs on .NET 8,
9 and 10. Source builds and installed-tool smoke tests cover each SDK/runtime in isolation.
Release packaging uses an isolated .NET 10 SDK. NuGet's default ZIP compression produced
252,322,360 bytes with SDK 10.0.401 for a payload that SDK 8.0.426 packed into 246,583,871 bytes.
The CLI's final pack target therefore recompresses the unsigned NuGet package with
`CompressionLevel.SmallestSize`. The .NET 10 candidate measured 244,880,053 bytes, without
removing any of its 448 members. Every member's SHA-256, length, order, name, timestamp,
external attributes and comment, plus the archive comment, are verified before atomic
replacement. Signed packages are refused; signing and attestation happen after packing.
Ordinary `dotnet pack` applies the same target without requiring a separate compression tool.

The tool contains the complete pinned Aspire 13.4.2 native tools trees for `win-x64`, glibc
`linux-x64`, `osx-x64` and `osx-arm64`. RID-specific publications contain only their own tree.
The path is `dcp/13.4.2/<rid>/tools/`, beside the host assembly; manifests, tunnel helpers, licences
and notices travel with the binaries. Private CLI-only package references avoid changing the
provider SDK dependency or ABI. Ordinary source builds keep their build-host metadata without
copying four large native trees into test output.

Resolution stays lazy inside inline Aspire startup. Priority is explicit `ASPIRE_DCP_PATH`, then
the host-relative installed bundle, then legacy source/custom-runner metadata and cache fallback.
An incomplete declared bundle fails without borrowing a warmed cache. Process architecture selects
the RID, including x64 processes under ARM64 emulation. NuGet tool startup restores Unix execute
bits only in its selected bundle; archives/installers preserve those bits during packaging.

## Release acceptance

- `scripts/check_distribution.py` checks the exact pinned version, RID set, executable platform,
  required files and native checksums. With `--nuget-cache`, it compares every shipped tools file
  with the exact restored upstream payload. The upstream osx-arm64 13.4.2 SBOM contains an incorrect
  NOTICE checksum; the original notice and manifest are preserved unchanged and checked against
  the restored package instead.
- NuGet.org documents a [250 MB maximum](https://learn.microsoft.com/en-us/nuget/nuget-org/nuget-org-faq).
  This repository enforces **250,000,000 bytes**, a conservative decimal ceiling. Exceeding it
  requires a reviewed distribution strategy; required native files must not be removed.
- The release workflow launches the candidate CLI and native DCP on all four advertised RIDs,
  verifies installer payloads, and runs a real Linux topology with an empty alternate cache,
  hidden build cache and fixtures outside the checkout. It requires executed passing scenarios
  and zero new containers/networks. Platform preflights alone do not prove Docker orchestration.

## Validation commands

```bash
python -m unittest discover -s scripts/tests -p test_check_distribution.py -v
python scripts/check_distribution.py --package artifacts/nuget/vouchfx.VERSION.nupkg --nuget-cache "$HOME/.nuget/packages"
python scripts/check_distribution.py --publish artifacts/publish/linux-x64 --rid linux-x64 --nuget-cache "$HOME/.nuget/packages"
```

See [installation](../getting-started.md#installing-vouchfx) and [DCP troubleshooting](../troubleshooting.md#the-aspire-orchestration-component-is-not-installed-could-not-be-located).
