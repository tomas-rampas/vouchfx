# Releasing vouchfx

This document covers how to cut a release, how consumers verify the artifacts,
the irreducible human steps that require provisioned credentials, and the
distinction between the primary (nupkg) and secondary (self-contained exe)
distribution channels.

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
    '^https://github.com/tomas-rampas/vouchfx/.github/workflows/release.yml@.*' \
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
NuGet package cache (`~/.nuget/packages/`).  This is automatically satisfied
for any developer who has previously built, restored, or used any .NET Aspire
project.  A completely fresh machine that has only the .NET 8 runtime and has
never resolved Aspire packages will need to run `dotnet restore` against a
project that references Aspire, or install the Aspire workload:

```bash
dotnet workload install aspire
```

### Secondary: self-contained executables and installers

**For machines without a .NET SDK, or for OS-native package management.**

The self-contained archives (`*.tar.gz`) and native installers (`.msi`,
`.deb`, `.pkg`) bundle the .NET 8 runtime alongside vouchfx.  Users do not
need the .NET SDK or runtime installed.

**DCP portability caveat (important):** the Aspire.AppHost.Sdk embeds the DCP
binary path as `AssemblyMetadata` (`dcpclipath`) at compile time, pointing to
the build machine's NuGet package cache.  This embedded path is machine-
specific.  The self-contained executables do NOT bundle the DCP binary itself.

The Windows MSI is the exception: it is built on a `windows-latest` runner,
so the embedded DCP path is a Windows absolute path
(`C:\Users\runneradmin\AppData\Local\...`) that matches the standard Windows
NuGet cache structure.  Users with `aspire.hosting.orchestration.win-x64`
v13.4.2 at their standard NuGet cache path will have DCP resolved correctly.

For Linux and macOS self-contained archives, the DCP path is embedded from
the `ubuntu-latest` runner's NuGet cache.  These archives work for users
whose NuGet cache is at `~/.nuget/packages/` (the standard location).

**Single-file executables are NOT produced** (e.g. `PublishSingleFile=true`
is not used).  The Roslyn compiler in the engine uses `Assembly.Location` to
discover provider DLLs; single-file mode returns an empty string for that API,
breaking provider loading with an `IL3000` build error.  This is a hard engine
invariant (see CLAUDE.md).

---

## Irreducible human and certificate steps

The following steps require provisioned credentials and cannot be automated
without those credentials.  The pipeline gates on them (steps are SKIPPED,
not failed, when the secret is absent) so releases still complete without
them.

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

### NuGet.org publish

**What:** Pushes the nupkg to NuGet.org so users can install via
`dotnet tool install -g vouchfx`.

**Secret required:**
- `NUGET_API_KEY` — a NuGet.org API key scoped to the `vouchfx` package ID.

**How to provision:**
- Log in to https://www.nuget.org with the account that owns the `vouchfx`
  package ID.
- Go to Account Settings > API Keys > Create.
- Scope the key to Push for the `vouchfx` package only.
- Add to GitHub repository secrets: `NUGET_API_KEY`.

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
