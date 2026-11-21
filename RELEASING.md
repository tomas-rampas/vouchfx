# Releasing vouchfx

This document covers how to cut a release, how consumers verify the artifacts,
the human provisioning steps (signing credentials and the NuGet Trusted
Publishing policy), and the distinction between the primary (nupkg) and
secondary (self-contained exe) distribution channels.

## How to trigger a release

The release pipeline (`release.yml`) publishes only when a GitHub Release is
published. Pushing a version tag alone does not start the pipeline. Manual
`workflow_dispatch` remains available as a non-publishing preflight.

1. In `CHANGELOG.md`, rename the `## [Unreleased]` heading to
   `## [<version>] — <date>` and add a fresh, empty `## [Unreleased]` heading
   above it, so the file keeps exactly one `## [Unreleased]` heading
   (`scripts/check_site.py` enforces this). Do this in the reviewed commit
   before tagging. Check that the `VouchfxEngineVersion` fallback in
   `Directory.Build.props` equals `<version>`: the
   `SourceCapabilityStamp_MatchesTheNewestChangelogVersion` test fails when the
   newest versioned heading and the fallback differ.
2. Navigate to the GitHub Releases page for this repository.
3. Click **Draft a new release**.
4. Create a new tag in the format `v<semver>` (e.g. `v1.2.3`) at the reviewed
   commit, or select an existing reviewed version tag.
5. Fill in the release notes. Mark alpha, beta and release candidate versions
   as pre-releases.
6. Click **Publish release**. Publishing a reviewed draft or creating a published
   Release through the GitHub API emits the same event.
7. The pipeline builds all artifacts, signs them, and attaches them to the
   release automatically.

A tag push followed by Release publication starts one publishing run. A newer
run does not automatically cancel an existing run (`cancel-in-progress: false`);
manual cancellation remains possible.

### Release-time documentation checklist

Some published text is worded to be true both before and after a release, and some can only be
finished once the tag is published. Work through this list in the release pull request or straight
after publication:

- Set `site/facts-fallback.json` (`engine_release`, `sdk_version`) to the newest **published**
  release (for example `v1.2.1` and `1.2.1`). It is used only when the live fact fetch fails, so it
  must never name an unpublished version.
- Name the first release that bundles a new Aspire patch line where the docs say "up to and including
  v1.2.1": `SECURITY.md` (the Aspire advisory section), the `CHANGELOG.md` Aspire entry, blueprint
  section 5.7 and the `NuGetAuditSuppress` comment in `Directory.Build.props`. Check that the
  `scripts/check_distribution.py` and `Vouchfx.Dcp.targets` comments still match the pinned Aspire.
- Update texts that say "once published": `SECURITY.md` and `docs/getting-started.md` (the
  `SslCa` advice for 1.2.1). Re-read the vouchfx-mcp paragraph in `docs/getting-started.md`: it
  states the exact-pin rule and names no version, so it needs an edit only if that rule changes.
- Confirm `move-floating-tag.yml` moved the floating `v1` tag, and that `docs/ci-integration.md` still
  describes the tags truthfully.
- In `vouchfx-mcp`, bump `ENGINE_PIN` and run `scripts/sync-vendored.ps1 -Update`; `docs/recipes.md`,
  `docs/language-reference.md` and the two composed-schema goldens are vendored byte-for-byte, so
  mention any change to them in that pull request. In `vouchfx-samples`, bump its engine pin.
- Re-read the .NET 10 wording in `docs/getting-started.md`, `docs/ci-integration.md` and
  `ci/gitlab/README.md`, and measure the .NET 10 default image on GitLab SaaS if a fresh receipt is wanted
  (the current receipts used `sdk:8.0`).
- Re-check the landing page (`site/index.html`) for statements that were worded around an unpublished
  release.

### Smoke-test without cutting a tag (`workflow_dispatch`)

- In the GitHub Actions UI, select `Release (signed, provenance)` and
  click **Run workflow**.
- Select the reviewed candidate ref and enter the version to validate.
- The pipeline builds all artifacts (nupkg, per-RID tarballs, SBOM, VSIX,
  MSI, deb, pkg), creates provenance attestations and runs distribution checks.
- **NuGet, GitHub Release and Marketplace publication are skipped.** The
  cosign steps are also skipped; optional certificate-based signing remains
  dependent on configured secrets.
- **Attestations are public records**, including on preflight runs; skipping
  package publication does not make the run private.
- Use this before publishing the next stable Release to validate the pipeline and native
  installer builds. It does not establish that the public packages are available.

---

## Artifact inventory

Each release attaches the following files:

| File | Description |
|------|-------------|
| `vouchfx.<ver>.nupkg` | dotnet global tool (primary distribution) |
| `vouchfx-<ver>-linux-x64.tar.gz` | Self-contained archive, Linux x64 |
| `vouchfx-<ver>-win-x64.tar.gz` | Self-contained archive, Windows x64 |
| `vouchfx-<ver>-osx-x64.tar.gz` | Self-contained archive, macOS x64 |
| `vouchfx-<ver>-osx-arm64.tar.gz` | Self-contained archive, macOS ARM64 |
| `vouchfx-<ver>-win-x64.msi` | Windows MSI installer |
| `vouchfx-<ver>-linux-x64.deb` | Debian/Ubuntu package |
| `vouchfx-<ver>-osx-arm64.pkg` | macOS package installer |
| `vouchfx-<ver>.bom.json` | CycloneDX SBOM (spec 1.7, JSON) |
| `vouchfx-<ver>.vsix` | VSCode extension |
| `*.cosign.bundle` | cosign keyless signature bundle per artifact |
| `*.nupkg.asc` | GPG detached signature for the nupkg (if `GPG_SIGNING_KEY` provisioned) |
| `*.deb.asc` | GPG detached signature for the .deb (if `GPG_SIGNING_KEY` provisioned) |

---

## Verifying artifacts

### Verify SLSA provenance (GitHub attestation)

Every artifact is attested with SLSA build provenance via
`actions/attest-build-provenance`.  Verification requires the `gh` CLI:

```bash
gh attestation verify vouchfx.1.2.3.nupkg \
  --repo tomas-rampas/vouchfx
```

Replace the filename with the artifact you want to verify.  A successful
verification prints the workflow that built the artifact and its git ref.

### Verify cosign keyless signature

Each artifact ships with a `.cosign.bundle` (self-contained: signature +
Fulcio certificate + Rekor inclusion proof).  Verification requires the
`cosign` CLI (install: `brew install cosign` / `winget install sigstore.cosign`):

```bash
cosign verify-blob vouchfx.1.2.3.nupkg \
  --bundle vouchfx.1.2.3.nupkg.cosign.bundle \
  --certificate-identity-regexp \
    '^https://github\.com/tomas-rampas/vouchfx/\.github/workflows/release\.yml@.*' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

A successful verification confirms:
- The file was produced by this repository's release workflow.
- The signature is anchored in the Sigstore Rekor public transparency log.
- No long-lived private key was involved.

### Verify GPG signature (nupkg, when provisioned)

If `GPG_SIGNING_KEY` was configured, a `vouchfx-<ver>.nupkg.asc` is also
attached.  To verify:

```bash
# Import the project's public key (published separately at keyserver or in docs/)
gpg --import vouchfx-public.asc
gpg --verify vouchfx.1.2.3.nupkg.asc vouchfx.1.2.3.nupkg
```

### Verify the SBOM

The `vouchfx-<ver>.bom.json` is a CycloneDX 1.7 JSON SBOM listing the
NuGet packages in the full solution dependency graph.  Inspect it with:

```bash
# Pretty-print
jq . vouchfx-1.2.3.bom.json | head -100

# Count components
jq '.components | length' vouchfx-1.2.3.bom.json

# Check for a specific package
jq '.components[] | select(.name == "Npgsql")' vouchfx-1.2.3.bom.json
```

Or load it into a CycloneDX-compatible tool (Dependency-Track, OWASP
Dependency-Check, Grype, Trivy, etc.) for vulnerability correlation.

---

## Primary vs secondary distribution

### Primary: dotnet global tool (nupkg)

**Recommended for developers with a .NET 10 SDK or runtime.**

```bash
dotnet tool install --global vouchfx
vouchfx --version
```

The tool is framework-dependent and requires .NET 10 or a newer major (runtime
or SDK) on the target machine; releases built before the move to .NET 10
require .NET 8. Stable Core distributions, beginning with `1.0.0`, bundle the engine DLLs and the complete,
version-matched DCP tools trees for `win-x64`, `linux-x64`, `osx-x64` and
`osx-arm64`. A source checkout or prior Aspire restore is not an installation
prerequisite. At topology start, `DcpPathResolver` selects the executing RID's
bundle beside the host assembly before stale build-machine cache metadata.
`ASPIRE_DCP_PATH` remains an explicit override for an existing DCP installation
(the directory containing `dcp`). The bundled tree is
`dcp/<VouchfxDcpVersion>/<rid>/tools/`. That property is 13.4.6 on current source, in lockstep
with `Aspire.Hosting` and `Aspire.AppHost.Sdk`; published releases through v1.2.1 bundle 13.4.2, and
a release cut from current source bundles 13.4.6. `AspireVersionLockstepGuardTests` fails the
unit-test run when `VouchfxDcpVersion`, the `Aspire.Hosting*` package versions (except
`Aspire.Hosting.Elasticsearch`) and the `Aspire.AppHost.Sdk` versions diverge.

**Legacy published rc.6:** this older package does not bundle DCP and requires
`aspire.hosting.orchestration.<rid>` version 13.4.2 in the executing machine's
NuGet cache, or an explicit `ASPIRE_DCP_PATH`. That legacy cache prerequisite
does not describe stable Core distributions from `1.0.0` onward. Installing the retired
Aspire workload does not provide the required NuGet package.

### Secondary: self-contained executables and installers

**For machines without a .NET SDK, or for OS-native package management.**

The self-contained archives (`*.tar.gz`) and native installers (`.msi`,
`.deb`, `.pkg`) bundle the .NET 10 runtime alongside vouchfx.  Users do not
need the .NET SDK or runtime installed.

**Bundled DCP portability:** current self-contained archives and installers
include the complete version-matched DCP tools tree for their own RID. They use
the same bundle-first resolution as the global tool; no pre-existing Aspire
NuGet cache is required. The `smoke-test-distribution-platforms` job in
`release.yml` installs the exact candidate into an isolated cache and launches
the bundled native DCP and archive CLI on all four advertised RIDs. The
`smoke-test-packaged-tool` job additionally runs the installed tool against a
live Docker topology with an empty user cache, checks the JUnit results and
rejects new Docker residue or a restored Aspire orchestration package. Both
jobs gate `publish-release`. Investigate the failing gate's logs before
retrying; a retry is not evidence that a portability defect is resolved.

**Single-file executables are NOT produced** (e.g. `PublishSingleFile=true`
is not used).  The Roslyn compiler in the engine uses `Assembly.Location` to
discover provider DLLs; single-file mode returns an empty string for that API,
breaking provider loading with an `IL3000` build error.  This is a hard engine
invariant (see CLAUDE.md).

---

## Human provisioning: certificate signing and trusted publishing

The following steps require provisioned credentials or human action on external
services.  The pipeline gates on them — signing steps are SKIPPED, not failed,
when the secret is absent, so releases still complete without them; NuGet
publishing succeeds via Trusted Publishing without any GitHub secret.

**Fail-closed behaviour — read before provisioning.**
When a signing secret is absent, the step is silently skipped and the release
proceeds with an unsigned artifact.  When a secret is *present but
misconfigured* (wrong passphrase, expired certificate, missing macOS
Developer-ID codesign pre-requisite, etc.) the signing step *fails* and the
entire release is blocked.  This is intentional — a broken signing
configuration must not produce an unsigned binary while appearing to have
signed it.

**Provision each signing method completely, or not at all.**
Each signing method requires two or more secrets (listed below).  Provisioning
only one of a pair (e.g. `CODESIGN_PFX_BASE64` without `CODESIGN_PFX_PASSWORD`)
will cause the step to run and fail immediately.  The `HAS_*` gate checks only
the primary secret; add all secrets for a method in the same step.

### Authenticode signing (Windows exe and MSI)

**What:** Signs `vouchfx.exe` and `vouchfx-<ver>-win-x64.msi` with an
Authenticode signature so Windows SmartScreen trusts the installer.

**Secrets required:**
- `CODESIGN_PFX_BASE64` — base64-encoded PKCS#12 (`.pfx`) certificate
- `CODESIGN_PFX_PASSWORD` — the certificate's password

**How to obtain a certificate:**
- Purchase an Extended Validation (EV) or Organisation Validation (OV) code-
  signing certificate from a CA trusted by Microsoft (DigiCert, Sectigo,
  GlobalSign, etc.).  EV certificates suppress SmartScreen immediately; OV
  certificates accumulate reputation over time.
- Or use Microsoft's Azure Trusted Signing service (formerly ACS):
  provision an Azure Trusted Signing account and replace the PFX-based signing
  steps in `release.yml` with the `azure/trusted-signing-action` action.

**How to provision:**
```bash
# Encode the .pfx file as base64
base64 -w 0 codesigning.pfx > codesigning.pfx.b64
# Add to GitHub repository secrets:
#   CODESIGN_PFX_BASE64   <- contents of codesigning.pfx.b64
#   CODESIGN_PFX_PASSWORD <- the certificate password
```

### Apple notarisation (macOS .pkg)

**What:** Submits the `.pkg` to Apple's Notary Service so macOS Gatekeeper
trusts the installer.

**Secrets required:**
- `APPLE_ID` — Apple ID e-mail address
- `APPLE_TEAM_ID` — 10-character Apple Developer team identifier
- `APPLE_APP_SPECIFIC_PASSWORD` — app-specific password generated at
  `appleid.apple.com > Security > App-Specific Passwords`

**How to obtain:**
- Enrol in the Apple Developer Programme (USD 99/year).
- Generate an app-specific password at https://appleid.apple.com.
- Your Team ID is visible in the Apple Developer portal.

**Note:** Apple notarisation also requires the binary (or the `.pkg`)
to be code-signed with a Developer ID certificate before submission.
The current skeleton does not include a code-signing step for macOS;
add `codesign --deep --sign "Developer ID Application: ..."` before
`pkgbuild` and `productsign` before notarytool if required.

### GPG signing (nupkg)

**What:** Produces a `.asc` detached signature for the nupkg so consumers
can verify the package without relying solely on NuGet's HTTPS.

**Secrets required:**
- `GPG_SIGNING_KEY` — base64-encoded armoured GPG private key
- `GPG_PASSPHRASE` — the key's passphrase

**How to provision:**
```bash
# Generate a dedicated signing key (or export an existing one)
gpg --gen-key
gpg --export-secret-keys --armor <key-id> | base64 -w 0 > signing-key.b64
# Add to GitHub repository secrets:
#   GPG_SIGNING_KEY <- contents of signing-key.b64
#   GPG_PASSPHRASE  <- the key passphrase
# Publish the public key to a keyserver or in docs/
gpg --export --armor <key-id> > docs/vouchfx-gpg-public.asc
```

### NuGet.org publish (Trusted Publishing)

**What:** Pushes the nupkg to NuGet.org so users can install via
`dotnet tool install --global vouchfx`.

**Secret required:** None — uses NuGet.org Trusted Publishing.

**How it works:**

The publish step uses NuGet.org's Trusted Publishing feature, which eliminates
the need for long-lived API keys.  The workflow carries `id-token: write`
permission and exchanges the GitHub OIDC token (single-use) for a short-lived
(~1 hour, reusable for multiple pushes) NuGet.org API key via the `NuGet/login`
action.  The `dotnet nuget push` command then uses that ephemeral key.

A Trusted Publishing policy was created on NuGet.org on 2026-07-05 with the
following settings:

| Field | Value |
|-------|-------|
| Package Owner | Tomas.R |
| Repository Owner | tomas-rampas |
| Repository | vouchfx |
| Workflow File | release.yml |
| Environment | (none) |

The workflow gates the login and push steps to the canonical repository
(`github.repository == 'tomas-rampas/vouchfx'`) and skips them on `workflow_dispatch`
smoke runs; the policy itself would accept any matching run of release.yml in this
repository regardless of trigger, so this workflow-side guard is what keeps smoke runs
from publishing. Forks would in any case fail the token exchange since the policy
names this repository.

**Policy state:** the policy became permanently active on the first successful publish, which locked
the GitHub repository and owner IDs in place. If a publish ever fails at the token exchange, check the
policy's state on the NuGet.org Trusted Publishing management page first.

---

## Documentation surfaces

The following topics belong in README.md or product documentation (not here):

- **Getting started / installation** — `dotnet tool install --global vouchfx`,
  system requirements.
- **Verifying a downloaded binary** — a shorter consumer-facing version of
  the cosign and gh-attestation verification commands above.
- **Upgrading** — `dotnet tool update --global vouchfx`.
- **Building from source** — for the self-contained exe use case.
- **How the bundled DCP works** — how stable distributions carry their own
  DCP tools, and the `ASPIRE_DCP_PATH` override, belong in the user docs, not
  in RELEASING.md.
