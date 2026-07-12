# Releasing vouchfx

This document covers how to cut a release, how consumers verify the artifacts,
the human provisioning steps (signing credentials and the NuGet Trusted
Publishing policy), and the distinction between the primary (nupkg) and
secondary (self-contained exe) distribution channels.

## How to trigger a release

The release pipeline (`release.yml`) fires on exactly two events:

1. **GitHub Release published** (recommended)
   - Navigate to the GitHub Releases page for this repository.
   - Click **Draft a new release**.
   - Create a new tag in the format `v<semver>` (e.g. `v1.2.3`).
   - Fill in the release notes.
   - Click **Publish release**.
   - The pipeline starts, builds all artifacts, signs them, and attaches
     them to the release automatically.

2. **Version tag pushed directly**
   ```
   git tag v1.2.3
   git push origin v1.2.3
   ```
   The pipeline creates a draft release, builds everything, and attaches the
   artifacts.  The maintainer should then review and publish the draft.

**Never push a `v*` tag without intending a release.**  The pipeline cannot
be cancelled mid-flight (concurrency policy: `cancel-in-progress: false`).

---

## First release after the Vouchfx.* rebrand (v1.0.0-alpha.4)

The v1.0.0-alpha.4 release marks the first publish of the `Vouchfx.*` package IDs after the pre-GA namespace rebrand from `Platform.*`. This section documents the one-time release steps specific to this transition.

### Pre-tag checklist

- **Confirm NuGet.org Trusted Publishing permits first-time package creation.** The existing Trusted Publishing policy (created 2026-07-05 for the `vouchfx` CLI nupkg) was granted by NuGet.org for this repository. Verify that:
  1. The policy's state is "active" on the NuGet.org Trusted Publishing page.
  2. The policy permits creation of new package IDs under the `Vouchfx.` prefix (empirical precedent: the same policy created the six `Platform.*` package IDs at alpha.3). If in doubt, contact NuGet.org support in advance.
  
- **Confirm no third party holds a prefix reservation on `Vouchfx.*`.** Query NuGet.org to ensure `Vouchfx.Sdk`, `Vouchfx.Sdk.Testing`, `Vouchfx.Engine.Abstractions`, `Vouchfx.Engine.Authoring`, and `Vouchfx.Engine.Compilation` are unclaimed. All five must resolve to "not found" before proceeding.

### Post-publish checklist

Once the v1.0.0-alpha.4 tag publishes and NuGet.org surfaces the five new `Vouchfx.*` packages, immediately execute these steps **on NuGet.org**:

1. **Unlist all versions of the six old package IDs:**
   - `Platform.Sdk` (all versions)
   - `Platform.Sdk.Testing` (all versions)
   - `Platform.Engine.Abstractions` (all versions)
   - `Platform.Engine.Authoring` (all versions)
   - `Platform.Engine.Compilation` (all versions)
   - `Community.Steps.JsonRpc` from the hub (if it was published as a separate NuGet ID; check current hub CI)

   Unlisting keeps exact-version `dotnet restore` commands working for downstream consumers (e.g. the hub's pinned alpha.3 restore stays green until its repin PR), while hiding them from search and latest-version resolvers.

2. **Set deprecation notices and alternate-package pointers on each unlisted package.** For each old ID:
   - Go to the package's NuGet.org page.
   - Mark it as **Deprecated**.
   - Set the **Deprecation Alternate Package** field to the corresponding `Vouchfx.*` successor:
     - `Platform.Sdk` → `Vouchfx.Sdk`
     - `Platform.Sdk.Testing` → `Vouchfx.Sdk.Testing`
     - `Platform.Engine.Abstractions` → `Vouchfx.Engine.Abstractions`
     - `Platform.Engine.Authoring` → `Vouchfx.Engine.Authoring`
     - `Platform.Engine.Compilation` → `Vouchfx.Engine.Compilation`
     - `Community.Steps.JsonRpc` → `Vouchfx.Community.JsonRpc` (if applicable)
   
   The alternate-package pointer is the primary migration path; NuGet.org displays it to users who have the old ID restored, with a clear link to upgrade.

3. **Note the version-line discontinuity in release notes.** The vouchfx CLI continues its version sequence: alpha.1 → alpha.2 → alpha.3 → alpha.4. However, the SDK and Engine packages are published for the *first time* under the `Vouchfx.*` names at v1.0.0-alpha.4 (there are no `Vouchfx.Sdk` alpha.1, alpha.2, or alpha.3 versions). The `Platform.*` package IDs carry alpha.1–alpha.3. Document this explicitly in the alpha.4 release notes to clarify the naming transition.

### Post-release follow-up

After the first successful `Vouchfx.*` publish and NuGet.org processing (typically within 1 hour), **apply for the `Vouchfx.` reserved ID prefix** on NuGet.org. This protects against third-party typosquatting. Submit a support request to NuGet.org via their website or contact form with:

- Requested prefix: `Vouchfx.`
- Justification: Brand protection for vouchfx open-source project.
- Current owner package: `Vouchfx.Sdk` (v1.0.0-alpha.4)

---

3. **Smoke-test without cutting a tag** (`workflow_dispatch`)
   - In the GitHub Actions UI, select `Release (signed, provenance)` and
     click **Run workflow**.
   - Enter a test version such as `0.0.0-test`.
   - The pipeline builds all artifacts (nupkg, per-RID tarballs, SBOM, vsix,
     MSI, deb, pkg) and runs the full sign-and-attest job.
   - **Release upload and NuGet push are skipped** on dispatch runs.
   - Use this before the first real `v1.0.0` tag to validate the pipeline end-
     to-end and confirm each OS installer builds on its native runner.

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

The `vouchfx-<ver>.bom.json` is a CycloneDX 1.7 JSON SBOM listing all 214+
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

**Recommended for developers with .NET 8 SDK.**

```bash
dotnet tool install -g vouchfx
vouchfx --version
```

The tool is framework-dependent and requires .NET 8 (runtime or SDK) on the
target machine.  All engine DLLs are bundled in the nupkg (~61 MB).

**DCP prerequisite:** the tool requires the Aspire DCP binary
(`aspire.hosting.orchestration.<rid>` v13.4.2) to be present in the local
NuGet package cache (`NUGET_PACKAGES` if set, otherwise `~/.nuget/packages/`).
The engine resolves it at run time: if the path baked in at pack time does not
exist on the executing machine (it never does for a CI-packed tool), the
`DcpPathResolver` fallback probes the local cache for the platform- and
version-exact package, and fails with an actionable environment error naming
the package if it is absent.  The prerequisite is satisfied on any machine
that has restored a project carrying `Aspire.AppHost.Sdk` 13.4.2 — the
version-exact requirement matters; having restored some *other* Aspire
version does not help.  A completely fresh machine populates the cache by
restoring this repository (`dotnet restore vouchfx.sln`) or any other
project carrying `Aspire.AppHost.Sdk` at that version.  Do **not** recommend
`dotnet workload install aspire`: the workload was retired with Aspire 9,
installs Aspire 8.2.x packs under the SDK packs directory (not the NuGet
cache), and can never satisfy this prerequisite.  Power users may instead
point `ASPIRE_DCP_PATH` at the directory of an existing DCP installation
(the folder containing the `dcp` executable); when that variable is set the
engine's fallback stands aside entirely and Aspire's own resolution uses it.

### Secondary: self-contained executables and installers

**For machines without a .NET SDK, or for OS-native package management.**

The self-contained archives (`*.tar.gz`) and native installers (`.msi`,
`.deb`, `.pkg`) bundle the .NET 8 runtime alongside vouchfx.  Users do not
need the .NET SDK or runtime installed.

**DCP portability (resolved at run time):** the Aspire.AppHost.Sdk embeds the
DCP binary path as `AssemblyMetadata` (`dcpclipath`) at compile time, pointing
to the *build machine's* NuGet package cache — an absolute, machine-specific
path with the build machine's RID.  The self-contained executables do NOT
bundle the DCP binary itself.  Historically this made every cross-machine
distribution channel silently broken (the baked `/home/runner/...` linux-x64
path can never exist on a user machine); the engine now self-heals at
topology start: when the baked path does not exist, `DcpPathResolver`
re-resolves DCP from the executing machine's own NuGet cache using the
executing machine's RID (see the DCP prerequisite above).  The
`smoke-test-packaged-tool` job in `release.yml` simulates exactly this
cross-machine hand-off — dead baked path, populated user cache — and gates
`publish-release`, so a portability regression can no longer ship.

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
`dotnet tool install -g vouchfx`.

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

**Pending-activation caveat:**

A newly created Trusted Publishing policy may start as "temporarily active" for
up to 7 days (this usually applies to private repositories); check the actual
state shown on the NuGet.org Trusted Publishing page.  It becomes permanently
active on the first successful publish, which locks the GitHub repository and
owner IDs in place.  If the policy shows as inactive and no publish has yet
occurred, the activation window can be restarted at any time from the
NuGet.org Trusted Publishing management page.

**Before cutting the `v1.0.0` tag,** log in to NuGet.org, navigate to the
Trusted Publishing settings, and verify the policy's state.  If it has lapsed
and no publish has succeeded, restart the activation window immediately.
After the first successful publish, the policy will remain active permanently.

---

## Documentation surfaces

The following topics belong in README.md or product documentation (not here):

- **Getting started / installation** — `dotnet tool install -g vouchfx`,
  system requirements, the Aspire DCP prerequisite for new users.
- **Verifying a downloaded binary** — a shorter consumer-facing version of
  the cosign and gh-attestation verification commands above.
- **Upgrading** — `dotnet tool update -g vouchfx`.
- **Building from source** — for the self-contained exe use case.
- **The DCP prerequisite explained for users** — "why do I need Aspire
  packages installed?" belongs in README/docs, not in RELEASING.md.
