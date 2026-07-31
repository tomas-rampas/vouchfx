# vouchfx Troubleshooting

This guide covers real failure modes, what they mean, and how to fix them.

**Quick index:**
- [Docker is not running or not reachable](#docker-is-not-running-or-not-reachable)
- [Transient image pull corruption: "short read" or "unexpected EOF"](#transient-image-pull-corruption-short-read-or-unexpected-eof)
- [EnvironmentError: HealthGate timeout of 00:00:20](#environmenterror-healthgate-timeout-of-000020)
- [Private registry and air-gapped operation](#private-registry-and-air-gapped-operation)
- [Discovery root does not exist (dotnet run path resolution gotcha)](#discovery-root-does-not-exist-dotnet-run-path-resolution-gotcha)
- [Script body and document size limits](#script-body-and-document-size-limits)
- [Understanding the four verdicts](#understanding-the-four-verdicts)
- [Secret leakage in exception messages](#secret-leakage-in-exception-messages)
- [Build fails with warnings-as-errors](#build-fails-with-warnings-as-errors)
- [Aspire topology timeouts or hangs](#aspire-topology-timeouts-or-hangs)
- [Steps run but assertions fail](#steps-run-but-assertions-fail)
- [Capture fails or placeholder is empty](#capture-fails-or-placeholder-is-empty)
- [Kafka messages not consumed (ordering or timing)](#kafka-messages-not-consumed-ordering-or-timing)
- [Validation errors at authoring time](#validation-errors-at-authoring-time)

---

## Docker is not running or not reachable

**Symptom:**
```
EnvironmentError: Failed to start topology — docker daemon unreachable
```

or

```
Cannot connect to the Docker daemon at unix:///var/run/docker.sock
```

**What it means:**
vouchfx orchestrates containers via Docker (Aspire + Testcontainers). If the Docker daemon is not running or not reachable, the topology cannot start.

**Fix:**

1. **Check Docker is running:**
   ```bash
   docker info
   ```
   You should see version and runtime information. If it fails, start Docker.

2. **On Windows / macOS:** Start Docker Desktop.

3. **On Linux:** Ensure the `dockerd` daemon is running:
   ```bash
   sudo systemctl start docker
   ```

4. **If Docker is running but still unreachable:** Check permissions. On Linux, the current user must be in the `docker` group:
   ```bash
   sudo usermod -aG docker $USER
   # Then log out and back in for the group change to take effect
   newgrp docker
   ```

5. **Verify the socket exists:**
   ```bash
   # Linux/macOS
   ls -la /var/run/docker.sock

   # Windows (Docker Desktop with WSL2)
   # The socket is exposed via npipe; Docker Desktop exposes it at the standard locations
   ```

**In CI:** Ensure the CI runner has Docker available. GitHub Actions' `ubuntu-latest` includes Docker by default. For GitLab, use `docker:dind` service or a socket-bind mount (see `README.md` § CI integration with GitLab CI).

---

## Transient image pull corruption: "short read" or "unexpected EOF"

**Symptom:**
```
ERROR: failed to build: failed to solve: failed to compute cache key:
short read: expected 12108405 bytes but got 11522155: unexpected EOF
```
or a `docker pull` / topology start failing with `unexpected EOF` or `failed to register layer`.

**What it means:**
A layer download was corrupted mid-transfer — a registry or network hiccup, not a vouchfx or
Dockerfile defect. When it happens during topology start (DCP's cold pull of a dependency
image), vouchfx reports it as an **EnvironmentError** — per the
[verdict taxonomy](#understanding-the-four-verdicts), infrastructure breakage, never a test
failure. When it happens while building your own application image, the build fails before
vouchfx runs at all.

**Fix:**

1. **Retry the pull or build** — a plain retry almost always succeeds; the corrupted partial
   layer is discarded and re-fetched.
2. **If it persists**, pull the base image explicitly to isolate the failing layer:
   ```bash
   docker pull <image>
   ```
   then check free disk space, and clear a corrupt build-cache entry with:
   ```bash
   docker builder prune
   ```
3. **In CI**, re-run the job. To reduce exposure on cold runners, pre-warm the images the
   suite needs (the reusable workflow's `prewarm-images` input does this; each pull is
   best-effort and non-fatal).

---

## The Aspire orchestration component is not installed / could not be located

**Symptom:**
```
System.IO.FileNotFoundException: The Aspire orchestration component is not installed
at "/home/runner/.nuget/packages/aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp".
```
or, from current engine versions:
```
EnvironmentError: The Aspire DCP orchestration component could not be located. …
no 'aspire.hosting.orchestration.<rid>' version '13.4.2' package was found at '…'.
```

**What it means:** vouchfx orchestrates containers through .NET Aspire's DCP binary, which ships in the `aspire.hosting.orchestration.<rid>` NuGet package and is looked up in your per-user NuGet cache (`NUGET_PACKAGES` if set, otherwise `~/.nuget/packages/`). The message means that package — at the exact pinned Aspire version and for *your* machine's platform — is not in your cache.

**Fix:** populate the cache once by restoring any project that carries `Aspire.AppHost.Sdk` at the pinned version, most simply this repository:

```bash
git clone https://github.com/tomas-rampas/vouchfx.git
dotnet restore vouchfx/vouchfx.sln
```

Version exactness matters: having restored some other Aspire version leaves a different version folder in the cache and does not resolve this error. The retired `dotnet workload install aspire` command does **not** help either — it installs Aspire 8.2.x packs outside the NuGet cache. If you keep DCP in a non-standard location, set the `ASPIRE_DCP_PATH` environment variable to the directory containing the `dcp` executable.

**If the message shows a `/home/runner/...` path (first variant above):** you are running `vouchfx` 1.0.0-alpha.5 or earlier from NuGet.org. Those pre-releases only consult a path baked in on the release build machine, so they fail on every other machine even with a fully populated cache. Fixed in 1.0.0-alpha.6 — upgrade (`dotnet tool update --global vouchfx --prerelease`), or build and run from source.

This failure is always classified as an **EnvironmentError** — infrastructure, not a product defect — so by default it does not break CI (see "Understanding the four verdicts" below).

For the full incident write-up — root cause, why the release pipeline missed it, and how regression is prevented — see the knowledge-base article [KB: DCP orchestrator not found](kb/dcp-orchestrator-portability.md).

---

## EnvironmentError: HealthGate timeout of 00:00:20

**Symptom:**
```
EnvironmentError: Failed to start topology — HealthGate timeout of 00:00:20 on resource 'postgres'
```

**What it means:**
This timeout is **not the vouchfx 120-second outer gate** — it is an **Aspire/DCP internal per-resource watchdog** (approximately 20 seconds per resource). When a resource takes longer than 20 seconds to become healthy (e.g., a large image pull on a cold cache, or a slow container startup), DCP's watchdog expires.

This is **NOT a vouchfx configuration knob** (there is no `--health-check-timeout` flag). The 20-second window is built into Aspire/DCP and cannot be extended at runtime.

**Fix:**

1. **Pre-warm Docker images locally before running the suite** (most common fix):
   ```bash
   # On your machine or in CI, before vouchfx runs:
   docker pull postgres:16
   docker pull myco/order-service:latest
   ```
   Once an image is in the local Docker cache, DCP skips the pull and starts the container much faster.

2. **In CI, use the `prewarm-images` workflow input** (GitHub Actions):
   ```yaml
   vouchfx-e2e:
     uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@<commit-sha>
     with:
       scenario-path: ./tests/e2e
       prewarm-images: |
         postgres:16
         myco/order-service:latest
   ```

   Or for GitLab CI, set `VOUCHFX_PREWARM_IMAGES`:
   ```yaml
   vouchfx-run:
     variables:
       VOUCHFX_PREWARM_IMAGES: |
         postgres:16
         myco/order-service:latest
   ```

3. **Check container startup performance locally.** If a container is consistently slow to start, it may have an expensive initialization step (e.g., schema migration, data load). Consider:
   - Running the initialization ahead of time (in the container image build, not at startup).
   - Using a healthcheck endpoint that returns quickly (not one that validates database connectivity, which adds latency).
   - Profiling the container with `docker logs` to see where time is spent.

4. **Serialize topology startup** if you are running tests in parallel locally. The `[assembly: CollectionBehavior(DisableTestParallelization=true)]` attribute (on xUnit test projects that use Aspire) serialises test startup, preventing concurrent DCP resource startup from overwhelming the machine and triggering timeouts. This is already applied in the vouchfx test projects (see `Vouchfx.Engine.Runtime.Tests` for the example).

5. **In CI, increase runner capacity or use a faster image pull network.** If your CI runner has constrained network or disk bandwidth, image pulls take longer. Use a faster runner if available, or pre-cache the image in the runner's local Docker registry.

**Why this happens:** Aspire's Distributed Cloud Provisioning (DCP) watches each resource and gives it ~20 seconds to become healthy. This is a safety net to prevent tests from hanging indefinitely. If multiple resources start concurrently, they compete for I/O (disk, network), and a large image pull can exceed 20 seconds. Pre-warming the cache is the most reliable fix.

---

## Private registry and air-gapped operation

**Symptom:**
Your organisation uses a private or internal container registry, and you need to run vouchfx tests without pulling images from Docker Hub or public registries. Alternatively, you work in an air-gapped environment where containers are pre-warmed locally and must not attempt external pulls.

**What it means:**
By default, vouchfx pulls container images from public registries (Docker Hub for unqualified names, or directly from the specified registry host). Teams on a private registry (Nexus, Artifactory, ECR, ACR) or in regulated/air-gapped environments need to redirect those pulls or enforce local-cache-only operation. The platform provides two complementary mechanisms: environment-level registry redirection (`imageRegistry`) for un-qualified images, and per-dependency image override (`image:` field) for explicit control.

**Fix:**

### 1. Redirect public images to an internal mirror (imageRegistry)

If your organisation mirrors public images on an internal registry, use the `imageRegistry` environment-level override to redirect un-qualified references:

```yaml
environment:
  imageRegistry: nexus.corp.local/docker-mirror

  services:
    my-api:
      image: myco/myapi:latest           # Will be redirected to nexus.corp.local/docker-mirror/myco/myapi:latest
      
  dependencies:
    db:
      type: postgres
      # Will use nexus.corp.local/docker-mirror/postgres (Aspire's default), not Docker Hub
```

The `imageRegistry` override applies to every un-qualified reference in both services and dependencies. Already-qualified references (those carrying a registry hostname) are never rewritten — they are pulled from their specified host as-is. When you specify a fully-qualified `image:` on a dependency, the engine also clears any built-in registry default the provider might carry, preventing unintended double-prefixing. The guarantee is: a fully-qualified `image:` is used exactly as written, with no prefixing from any source.

### 2. Override dependency images with per-dependency `image:` field

For finer-grained control — when you need a specific version or a non-standard image for one dependency — use the `image:` field on individual dependencies:

```yaml
environment:
  dependencies:
    orders-db:
      type: postgres
      image: nexus.corp.local:5000/platform/postgres:16-custom    # Explicit override
    
    events:
      type: kafka
      image: artifactory.mycompany.com/confluent/kafka:7.5.0       # Pulls from Artifactory
    
    cache:
      type: redis
      version: "7"                                                  # Uses Aspire's default: redis:7
```

The per-dependency `image:` field bypasses Aspire's provisioned default entirely. An `image:` carrying no tag or digest can be paired with a `version:` field; `version:` supplies the tag. If **both** `image:` (with a tag) **and** `version:` are set, that is rejected as ambiguous.

### 3. Enforce local-cache-only operation

For fully air-gapped or pre-warmed environments, use `imagePullPolicy: Never` to prevent unexpected outbound pulls:

```yaml
environment:
  imagePullPolicy: Never    # All images must be pre-warmed locally; no external pulls allowed
  
  services:
    my-api:
      image: myco/myapi:v1.2.3
      
  dependencies:
    db:
      type: postgres
      version: "16"
```

If an image is not present locally, the topology will fail to start. Ensure all required images are pre-warmed on the host:

```bash
# Before running tests, pre-warm all images
docker pull myco/myapi:v1.2.3
docker pull postgres:16
docker pull redis:7
```

### 4. Known limitation: sidecar container images

Two resource types provision sidecar containers whose images **cannot** be overridden:

- **`kafka` with `schemaRegistry: true`** — Aspire provisions a Confluent Schema Registry sidecar. The schema-registry image is fixed.
- **`azureservicebus`** — Aspire provisions a SQL Edge sidecar for emulation. The SQL Edge image is fixed.

If you rely on either resource and must run it on a private registry, your sidecar's image cannot currently be customised. This is a known limitation; please open an issue on the GitHub repository if this blocks your workflow.

### Best practices for private registry operation

1. **Use `imageRegistry` for wholesale redirects** when you have a private mirror of all public images. This keeps scenarios concise.

2. **Use per-dependency `image:`** when you need per-resource control, or when not all images are mirrored (only some dependencies go to the internal registry).

3. **Combine both:** `imageRegistry` as a safety default, and per-dependency `image:` for exceptions:
   ```yaml
   environment:
     imageRegistry: mirror.internal.corp    # Fallback for any un-qualified reference
     dependencies:
       orders-db:
         type: postgres                     # Will use mirror.internal.corp/postgres
       events:
         type: kafka
         image: kafka.example.com/cp-kafka:7.5  # Override: this one comes from a different host
   ```

4. **Prefer digest pinning** (`@sha256:…`) over tags in air-gapped environments; digests are byte-stable and prevent accidental pulls of newer versions if a tag is reused.

5. **Document your registry strategy** in your test suite README so teammates understand which images are sourced from where.

---

## Discovery root does not exist (dotnet run path resolution gotcha)

**Symptom:**
```
Discovery root './tests/e2e' does not exist
```

when running:
```bash
dotnet run --project src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -- run ./tests/e2e
```

**What it means:**
This gotcha is specific to running the CLI from source via `dotnet run --project`. When you use `dotnet run`, it changes the working directory to the **project directory** (`src/Cli/Vouchfx.Cli/`), not your current directory. So a relative path like `./tests/e2e` resolves relative to that project directory, not your repository root, and fails.

If you are using the packaged `vouchfx` CLI (installed via `dotnet tool install`), you will not encounter this issue — the tool runs from your current working directory, so relative paths work as expected.

**Fix:**

Use one of these approaches:

1. **Use the packaged vouchfx CLI (recommended):**
   ```bash
   # Install (pre-release channel — required whilst published versions are 1.0.0-alpha.x)
   dotnet tool install --global vouchfx --prerelease

   # Already installed? Upgrade instead
   dotnet tool update --global vouchfx --prerelease

   # Then run from your current directory
   vouchfx run ./tests/e2e
   ```

   The packaged tool runs from your current directory, so relative paths behave as expected and this gotcha never applies.

2. **If you are contributing to vouchfx from source,** build the CLI once and run the binary directly:
   ```bash
   # Build once
   dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release

   # Run the built binary
   src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx run ./tests/e2e
   ```
   (On Windows: `src\Cli\Vouchfx.Cli\bin\Release\net8.0\vouchfx.exe run .\tests\e2e`)

   The binary runs from your current directory, so relative paths work as expected.

3. **Pass an absolute path to `dotnet run`:**
   ```bash
   dotnet run --project src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -- run $(pwd)/tests/e2e
   ```
   (On Windows: `dotnet run --project src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -- run $((Get-Location).Path)/tests/e2e`)

**Best practice:** For running vouchfx test suites, use the packaged CLI — it is simpler, requires no path-resolution workarounds, and works on any machine with .NET 8 installed. If you are working on vouchfx itself from source, build the CLI once and run the binary directly — it is faster (skips compilation) and avoids `dotnet run` quirks altogether.

---

## Script body and document size limits

**Symptom:**

```
script.csharp: 'code' size 72000 characters exceeds the 65536-character limit (a plain resource bound, not a security control — see this file's header comment); reduce its size or split the script.
```

or

```
File size 1200000 bytes exceeds the 1048576-byte (1 MiB) limit for a single *.e2e.yaml document (a guard against pathological input); split the suite into smaller files.
```

**What it means:**

The engine enforces two resource-limit bounds before compilation: a maximum of 64 KiB per `script.csharp` step body (inline `code` or referenced `file:`), and 1 MiB per `.e2e.yaml` document. These are sanity bounds to prevent accidentally passing pathologically large files to the compiler, not a defence against deliberate crash or hang attempts — which can occur well under these sizes (e.g. a ~100-character nested string interpolation can hang the parse).

When a limit is exceeded, the scenario is marked **Inconclusive** on `run` (exit code 4 with `--fail-on-inconclusive`), and **invalid** on `validate` (exit code 4). Validation completes normally and names the specific limit — it is not a crash.

**Fix:**

1. **Script body too large?** Split a large `script.csharp` body across multiple steps:
   ```yaml
   - id: validate-part-1
     type: script.csharp
     code: |
       // First portion of logic

   - id: validate-part-2
     type: script.csharp
     code: |
       // Next portion of logic
   ```

2. **Document too large?** Split into separate `.e2e.yaml` files:
   ```bash
   # Before: one large scenario file (1.5 MiB)
   # After: split by concern
   create-tests.e2e.yaml       # Creation tests (~200 KiB)
   fulfillment-tests.e2e.yaml  # Fulfillment tests (~300 KiB)
   audit-tests.e2e.yaml        # Audit trail tests (~250 KiB)
   ```

---

## Understanding the four verdicts

vouchfx distinguishes four outcomes (see `docs/01` §12.1 for the full taxonomy):

| Verdict | Meaning | Example | Default CI exit code |
|---------|---------|---------|----------------------|
| **Pass** | All assertions passed. | A test runs end-to-end and all steps succeed. | 0 (success) |
| **Fail** | An assertion failed — a genuine product defect. | `expect: { status: 200 }` but the API returned 500. | 1 (always breaks CI) |
| **EnvironmentError** | Infrastructure problem, not a product defect. | Docker daemon unreachable, image pull fails, seed SQL fails. | 0 by default; 3 if `--fail-on-env-error` |
| **Inconclusive** | The engine could not decide; the assertion may pass if retried. | A RETRY step's polling window expires; a capture expression fails to match. | 0 by default; 4 if `--fail-on-inconclusive`; unconditionally 4 if every scenario fails to parse |

**Why the distinction?**

In microservices, infrastructure is often brittle. A test might fail not because the code is wrong, but because Docker is slow or a database is down. The vouchfx verdict taxonomy lets your CI system handle each case independently:

- **Fail (1)** — Your code broke. Fix it.
- **EnvironmentError (0 or 3)** — The infrastructure is in trouble. Page on-call or escalate to DevOps.
- **Inconclusive (0 or 4)** — The engine could not decide; maybe timing was just off. Investigate or re-run.

By default, **only Fail breaks CI**. This reduces false positives and keeps developers focused on real defects, not infrastructure flakiness.

**Opt into stricter gating with flags:**
```bash
# Fail breaks CI; environment errors do not (default)
vouchfx run ./tests

# Also break CI on environment errors
vouchfx run ./tests --fail-on-env-error

# Also break CI on inconclusive results
vouchfx run ./tests --fail-on-inconclusive

# Both
vouchfx run ./tests --fail-on-env-error --fail-on-inconclusive
```

---

## Secret leakage in exception messages

**Symptom:**
A `script.csharp` step throws an exception with a secret value in the message. The exception message is recorded as an observation in the `--events` JSON Lines output, where it becomes visible.

**Example:**
```csharp
- id: validate-token
  type: script.csharp
  code: |
    var token = Vars.Secrets.Resolve("vault/api-token").Reveal();
    if (!token.StartsWith("sk_test_"))
      throw new Exception($"Invalid token: {token}");  // DANGER: reveals the token
```

**What it means:**
Unlike the terminal output and HTML report (which redact secret values), the `--events` JSON Lines stream persists **raw observations verbatim**. If a script throws an exception with a revealed secret in its message, that message becomes a raw observation in the event stream.

**Fix:**

1. **Never embed resolved secret values in exception messages.** Use the reference path or a generic error message instead:
   ```csharp
   // Good: mentions the reference, not the resolved value
   throw new Exception("Token validation failed (source: vault/api-token)");

   // Also good: no secret mention at all
   if (!IsValidToken(token))
     throw new Exception("Invalid token format");
   ```

2. **The only deliberate escape hatch is `SecretString.Reveal()`.** Call it only at the moment you inject the value into a sink (e.g. an HTTP Authorization header), never write the revealed value back into `Vars` or any logged/serialised structure:
   ```csharp
   var token = Vars.Secrets.Resolve("vault/api-token").Reveal();
   // Use token here at the injection point only; never store or log the revealed value
   if (!IsValidToken(token))
     throw new Exception("Token validation failed");  // No secret in the message
   ```

3. **Understand the three tiers of secret protection** (see `docs/01` §17):
   - **Tier 1: No bake into IL** — The secret value is never compiled into the C# source or IL (verified by SecretResolutionPipelineTests).
   - **Tier 2: Redaction in output** — Terminal output, HTML report, and JUnit XML redact secret values (shown as "(redacted)").
   - **Tier 3: Raw event stream** — `--events` JSON Lines persists observations verbatim; authors must ensure no exception messages leak secrets.

**Best practice:** Treat exception messages as user-visible; never embed secrets in them. The reproducibility envelope records the **reference hash**, not the value — use that for reproducibility without embedding secrets.

---

## Build fails with warnings-as-errors

**Symptom:**
```
build.csproj : error : Treating warnings as errors.
CSC5001: warning CS8360: [details...] cannot have...
```

**What it means:**
The vouchfx codebase is compiled with `/WarnAsError` enabled (zero-warning policy). Any compiler warning (including `#nullable`, unused variable, etc.) is treated as a build error.

**Fix:**

1. **Address the root cause:** Read the warning message and fix it. Common examples:
   - Unused `using` statement → remove it.
   - Null-reference warning → add a null check or `!` operator.
   - Unused variable → remove it or prefix with `_`.

2. **If the warning is spurious or unavoidable:** Suppress it locally with `#pragma`:
   ```csharp
   #pragma warning disable CS8360
   // Your code here
   #pragma warning restore CS8360
   ```

3. **Run the format gate locally before pushing:**
   ```bash
   dotnet format --verify-no-changes
   ```
   This catches formatting issues (and indirectly, some warnings) early.

**Why the strict policy?** Zero-warning builds improve code quality and make intentional (narrowly-scoped) suppressions stand out in code review.

---

## Aspire topology timeouts or hangs

**Symptom:**
Tests hang or timeout waiting for the topology to start. The Aspire dashboard (if enabled) shows resources stuck in a transitional state.

**What it means:**
A service or dependency failed to start or became unhealthy. Common causes:

1. **A service depends on another service that is not declared.** Aspire cannot auto-discover undeclared dependencies.
2. **A healthcheck endpoint is broken.** vouchfx health-gates ports; if the port is open but the healthcheck fails, Aspire retries indefinitely.
3. **A container image does not exist or cannot be pulled.** `docker pull` fails silently in some Aspire configurations.

**Fix:**

1. **Run with the `--events` flag to inspect observations** to see which resource is stuck:
   ```bash
   vouchfx run ./tests --events ./events.jsonl
   ```
   Then examine `events.jsonl` for `step-attempt` records showing stuck health-check attempts or failed container starts.

2. **Verify all dependencies are declared in `environment.services` and `environment.dependencies`.** If a service tries to connect to a database not in the environment, the topology cannot satisfy it.

3. **Verify the healthcheck endpoint is fast and works.** Test it manually:
   ```bash
   docker run -d -p 8080:8080 myco/myservice:latest
   curl http://localhost:8080/health
   ```
   If the healthcheck endpoint hangs or fails, the container will be marked unhealthy.

4. **Check image availability:**
   ```bash
   docker pull myco/myservice:latest
   ```
   If the pull fails, the topology cannot start.

---

## Steps run but assertions fail

**Symptom:**
A step runs (e.g., an HTTP request succeeds with status 200) but the assertion fails:
```
Assertion failed: expected status 201, got 200
```

**What it means:**
The step executed successfully, but the response did not match the expected conditions. This is a **Fail** verdict — a genuine product defect, not an infrastructure problem.

**Fix:**

1. **Review the assertion.** Is it correct for the current test? `http.rest` asserts the
   status code only — to check a response body, `capture` the value and assert it in a
   later `script.csharp` step (or a `db-assert`).
   ```yaml
   expect:
     status: 201  # Should this be 200?
   ```

2. **Examine the actual response.** Run the step in isolation (if possible) and log the full response:
   ```yaml
   - id: debug-response
     type: http.rest
     target: api
     method: GET
     path: /data
     # No expect block — just capture the response
     capture:
       response_body: "$"

   - id: log-it
     type: script.csharp
     code: |
       var resp = (string)Vars["response_body"];
       System.Console.WriteLine("Response: " + resp);
   ```

3. **Check for state threading issues.** If an earlier step's capture is empty, a placeholder substitution may fail:
   ```yaml
   - id: create
     type: http.rest
     target: api
     method: POST
     path: /resources
     capture:
       resource_id: "$.id"  # Did this JSONPath actually match?

   - id: fetch
     type: http.rest
     target: api
     method: GET
     path: "/resources/{resource_id}"  # Is {resource_id} really set?
   ```

4. **Re-run the scenario with `--events` to capture raw observations:**
   ```bash
   vouchfx run ./tests --events ./events.jsonl
   ```
   The JSON Lines file contains every step observation; examine it for the actual response.

---

## Capture fails or placeholder is empty

**Symptom:**
```
Capture failed: JSONPath '$.missing_field' did not match the response
```

or a placeholder resolves to empty/null in a later step.

**What it means:**
A capture expression (JSONPath or XPath) did not match the step result, or a placeholder references a non-existent variable.

**Fix:**

1. **Verify the JSONPath is correct.** Use a JSONPath tester (e.g., [jsonpath.com](https://jsonpath.com)) to test the expression against your response:
   ```json
   // Response
   { "user": { "id": 123, "name": "Alice" } }

   // Correct path
   $.user.id  // → 123

   // Incorrect path
   $.id       // → null (user is nested)
   ```

2. **Check the response structure.** The actual response may differ from what you expected:
   ```yaml
   capture:
     id: "$.id"  # Response has { "userId": 123 }, not { "id": 123 }
   ```

3. **Use optional captures if a field might not always be present:**
   ```yaml
   # If you want the capture to not fail when the field is missing,
   # use a safe JSONPath expression that returns a default:
   capture:
     error_code: "$.error.code"  # If 'error' is missing, the capture fails
     # Instead, check the response structure first in a script:
   ```

4. **Use `continueOnFailure` if a capture is truly optional:**
   ```yaml
   - id: get-optional-data
     type: http.rest
     target: api
     method: GET
     path: /data
     expect:
       status: 200
     capture:
       optional_field: "$.optional"
     continueOnFailure: true  # Step passes even if capture fails
   ```

5. **Verify variable names match.** Placeholders are case-sensitive:
   ```yaml
   capture:
     UserId: "$.id"  # Captured as "UserId"

   # Later step
   path: "/users/{userid}"  # ERROR: {userid} ≠ {UserId}
   ```

---

## Kafka messages not consumed (ordering or timing)

**Symptom:**
An `mq-expect.kafka` step fails to find a message that was published earlier, even with `verifyMode: RETRY`.

**What it means:**
Common causes:

1. **Message was published before the consumer started listening.** Kafka does not replay historical messages by default (unless `earliest` is configured).
2. **Topic does not exist.** The message was published to a different topic.
3. **Key or match criteria are too strict.** The message exists but does not match the filter.
4. **Timing issue.** The publish step and expect step are running concurrently; the expect starts before the publish completes.

**Fix:**

1. **Ensure the order: publish → expect.** In your steps, publish first, then expect:
   ```yaml
   - id: publish-order-event
     type: mq-publish.kafka
     target: kafka
     topic: orders.created
     payload: |
       { "orderId": "123", "status": "new" }
     key: "123"

   # Later step (runs after publish completes)
   - id: expect-order-event
     type: mq-expect.kafka
     target: kafka
     topic: orders.created
     match:
       key: "123"
     verifyMode: RETRY
     timeout: 10s
   ```

2. **Use a consistent topic name.** Verify the publish and expect steps reference the same topic:
   ```yaml
   - id: publish
     type: mq-publish.kafka
     topic: order.created  # All lowercase

   - id: expect
     type: mq-expect.kafka
     topic: order.created  # Must match exactly
   ```

3. **Relax the match criteria temporarily to debug.** Remove the `key` or other filters to see if the message exists:
   ```yaml
   match:
     # Removed strict key filter for debugging
     bodyContains: "orderId"
   ```

4. **Use `verifyMode: RETRY`** with a reasonable `timeout` (e.g., 10–30 seconds):
   ```yaml
   - id: expect-with-polling
     type: mq-expect.kafka
     target: kafka
     topic: orders.created
     match:
       key: "123"
     verifyMode: RETRY
     timeout: 10s  # Wait up to 10 seconds, polling with backoff
   ```

5. **Check Kafka broker health.** If the broker is slow or unhealthy, messages may be delayed:
   ```bash
   docker logs <kafka-container-id>
   ```

6. **Understand Kafka's offset management.** By default, vouchfx's Kafka consumer seeks to the latest offset. If a publish step and an expect step both run in the same scenario, the consumer might miss the message if it subscribes *before* the message is published. Ensure publish runs first.

---

## RabbitMQ: message not in queue (ownership, durability, publish-after-declare)

**Symptom:**
An `mq-expect.rabbitmq` step asserts on a queue but the message is not found, even though the publish step ran first.

**What it means:**
Common causes:

1. **Queue does not exist.** RabbitMQ routing requires the queue to exist before a message can be routed to it.
2. **Durability mismatch.** The queue was declared as transient (auto-delete) in a previous test and was deleted when emptied.
3. **Wrong routing key.** The routing key used in the publish step does not match the queue name or binding.
4. **Message was already consumed.** Another consumer drained the queue before the assertion ran.

**Fix:**

1. **Declare the queue explicitly in the environment.** If your SUT declares the queue at startup, ensure the HTTP trigger step runs before the assertion:
   ```yaml
   steps:
     # Step 1: Trigger SUT to declare the queue
     - id: initialize-service
       type: http.rest
       target: rabbitmq-service
       method: POST
       path: /init
       expect:
         status: 200

     # Step 2: Now the queue exists; publish a message
     - id: publish-message
       type: mq-publish.rabbitmq
       target: events-rmq
       routingKey: orders-notifications
       payload: '{"orderId":"123"}'

     # Step 3: Assert the message is in the queue
     - id: assert-message
       type: mq-expect.rabbitmq
       target: events-rmq
       queue: orders-notifications
       match:
         payloadContains: "123"
       verifyMode: RETRY
       timeout: 10s
   ```

2. **Use durable queues.** In your SUT or topology setup, declare queues with the durable flag set to `true`:
   ```csharp
   // Example: declaring a durable queue in RabbitMQ client
   channel.QueueDeclare(
       queue: "orders-notifications",
       durable: true,
       exclusive: false,
       autoDelete: false
   );
   ```

3. **Match the routing key to the queue name.** When using the default exchange, the routing key must match the queue name exactly:
   ```yaml
   - id: publish
     type: mq-publish.rabbitmq
     routingKey: orders-notifications  # Routes to this queue name

   - id: assert
     type: mq-expect.rabbitmq
     queue: orders-notifications  # Same as routing key
   ```

4. **Use `verifyMode: RETRY`** to wait for eventual consistency:
   ```yaml
   - id: assert-with-retry
     type: mq-expect.rabbitmq
     target: events-rmq
     queue: orders-notifications
     match:
       payloadContains: "123"
     verifyMode: RETRY
     timeout: 15s
   ```

---

## NATS: Inconclusive verdict on mq-expect.nats (stream created after publish — lost message)

**Symptom:**
An `mq-expect.nats` step times out with an `Inconclusive` verdict, but the message was published in a preceding step.

**What it means:**
NATS JetStream messages are **only retained if the stream exists before they are published.** If you publish to a subject before the stream is created, the message is lost and cannot be recovered. The step then polls indefinitely and times out.

**Common scenario:**
1. Publish step runs and tries to publish to `orders.created`.
2. The provider creates the stream on first use (lazy initialisation).
3. But if there is a race or ordering issue, the stream might not exist yet → message is lost.
4. Expect step runs later, creates the stream, and polls — but the message is gone.

**Fix:**

1. **Ensure the stream exists before publishing.** The simplest approach is to have an expect step run *before* the publish to warm up the stream:
   ```yaml
   steps:
     # Step 1: Warm up the stream (ensures it exists)
     - id: ensure-stream-exists
       type: mq-expect.nats
       target: nats-broker
       subject: orders.created
       match:
         payloadContains: "dummy"  # A dummy criterion that will fail
       continueOnFailure: true  # We expect this to fail; we just want the stream created

     # Step 2: Now publish (stream is guaranteed to exist)
     - id: publish-order-event
       type: mq-publish.nats
       target: nats-broker
       subject: orders.created
       payload: '{"orderId":"123"}'

     # Step 3: Assert (stream exists and message is retained)
     - id: assert-order-event
       type: mq-expect.nats
       target: nats-broker
       subject: orders.created
       match:
         payloadContains: "123"
       verifyMode: RETRY
       timeout: 10s
   ```

2. **Use explicit stream names to avoid derivation ambiguity.** When two subjects might derive to the same stream name, use explicit names:
   ```yaml
   - id: publish
     type: mq-publish.nats
     target: nats-broker
     subject: orders.created
     stream: ORDERS_CREATED  # Explicit name prevents collisions

   - id: assert
     type: mq-expect.nats
     target: nats-broker
     subject: orders.created
     stream: ORDERS_CREATED  # Must match the publish step
   ```

3. **Understand stream name derivation.** Subject names are converted to stream names by uppercasing and replacing non-alphanumeric characters (except `-`) with underscores. For example:
   - `orders.created` → `ORDERS_CREATED`
   - `orders_created` → `ORDERS_CREATED` (collision!)
   - `orders-created` → `ORDERS-CREATED` (different from `orders.created`)

   When in doubt, use explicit `stream` names in both publish and expect steps.

4. **Use separate NATS dependencies per scenario.** If you have multiple scenarios asserting on the same subject, give each scenario its own `nats` dependency to avoid cross-scenario message bleed:
   ```yaml
   # Scenario 1
   environment:
     dependencies:
       nats-scenario-1:  # Separate instance
         type: nats

   # Scenario 2
   environment:
     dependencies:
       nats-scenario-2:  # Separate instance
         type: nats
   ```

---

## Azure Service Bus: entity not declared in dependency config → EnvironmentError

**Symptom:**
```
EnvironmentError: Failed to start topology — queue 'my-queue' not found on service-bus dependency
```

**What it means:**
Azure Service Bus Emulator (and real Azure Service Bus) requires queue and topic declarations to be made explicit in the test's `environment.dependencies` section. Queues and topics that are not declared will not be created, and messages sent to them will fail with an EnvironmentError.

**Fix:**

1. **Declare all queues and topics in the environment.** List them explicitly under the `azureservicebus` dependency:
   ```yaml
   environment:
     dependencies:
       service-bus:
         type: azureservicebus
         queues:
           - order-commands
           - order-notifications
         topics:
           - name: order-events
             subscriptions:
               - fulfillment-sub
               - billing-sub
           - name: shipment-events
             subscriptions:
               - tracking-sub
   ```

2. **Match queue and topic names exactly.** Ensure the `mq-publish` and `mq-expect` steps reference the declared names:
   ```yaml
   steps:
     - id: publish
       type: mq-publish.azureservicebus
       target: service-bus
       queue: order-commands  # Must be declared above

     - id: assert
       type: mq-expect.azureservicebus
       target: service-bus
       queue: order-commands  # Same name
   ```

3. **For topic subscriptions, declare both the topic and subscription.** A subscription cannot be created without its parent topic:
   ```yaml
   environment:
     dependencies:
       service-bus:
         type: azureservicebus
         topics:
           - name: order-events
             subscriptions:
               - fulfillment-sub  # This subscription must have a parent topic
   ```

4. **Understand the entity declaration syntax.**
   - **Queues** are simple strings: `queues: [queue1, queue2, ...]`
   - **Topics** are objects with a `name` and optional `subscriptions` array: `topics: [{name: topic1, subscriptions: [sub1, sub2]}]`

---

## Redis: AuthenticationError from the SUT (missing password credential)

**Symptom:**
Your system under test fails with an authentication error when connecting to Redis:
```
AuthenticationError: WRONGPASS invalid username-password pair
```

or

```
ERR invalid password
```

**What it means:**
The managed Redis dependency (provided by Aspire/Testcontainers) has authentication enabled, and your SUT is either:
1. Not providing the password at all.
2. Using the wrong password or username.

**Fix:**

1. **Inject the Redis password via environment variable.** The engine stages the Redis connection string (including password) as `conn::<target>`. Inject it into your SUT:
   ```yaml
   environment:
     services:
       my-service:
         image: myco/my-service:latest
         env:
           REDIS_CONNECTION_STRING: ${conn:cache}
   ```

   Your SUT should then parse this connection string and use it to connect.

2. **If your SUT requires host, port, and password separately,** extract them:
   ```yaml
   environment:
     services:
       my-service:
         image: myco/my-service:latest
         env:
           REDIS_HOST: ${conn:cache.host}
           REDIS_PORT: ${conn:cache.port}
           REDIS_PASSWORD: ${conn:cache.password}
   ```

3. **For .NET applications using StackExchange.Redis,** construct the connection options from the credentials:
   ```csharp
   string connString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
   var options = ConfigurationOptions.Parse(connString);
   var redis = ConnectionMultiplexer.Connect(options);
   ```

4. **If the error persists,** verify the Redis container is healthy:
   ```bash
   docker logs <redis-container-id>
   redis-cli -h localhost -p 6379 -a <password> ping
   ```

---

## SQL Server: slow cold start; EnvironmentError on first pull

**Symptom:**
```
EnvironmentError: Failed to start topology — HealthGate timeout of 00:00:20 on resource 'sqlserver'
```

The SQL Server container takes a long time to start for the first time.

**What it means:**
SQL Server is a large image (multiple gigabytes) and has a lengthy initialisation sequence. On the first run, Docker must pull the image from the registry, which can exceed the Aspire/DCP 20-second health check window.

**Fix:**

1. **Pre-warm the SQL Server image locally before running the suite** (most effective):
   ```bash
   docker pull mcr.microsoft.com/mssql/server:2022-latest
   ```
   Once the image is in the local cache, subsequent container startups are much faster.

2. **In CI, use the `prewarm-images` workflow input** (GitHub Actions):
   ```yaml
   vouchfx-e2e:
     uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@<commit-sha>
     with:
       scenario-path: ./tests/e2e
       prewarm-images: |
         mcr.microsoft.com/mssql/server:2022-latest
   ```

3. **Ensure sufficient disk space and memory.** SQL Server requires at least 2 GB RAM and adequate disk space. If your Docker daemon is resource-constrained, increase limits.

4. **Understand the startup sequence.** SQL Server's health check waits for the database engine to be ready, which involves filesystem initialisation. This is non-configurable; the 20-second Aspire window is a hard limit.

---

## SMTP/Mailpit: mail not found (wrong target key — HTTP API vs. SMTP endpoint)

**Symptom:**
Your system sends an email via SMTP, but the `mail-expect.smtp` step finds no messages.

**What it means:**
Common causes:

1. **Your SUT is not connecting to Mailpit.** The SMTP endpoint is not injected or the SUT is ignoring it.
2. **Wrong environment variable names.** The SUT expects different variable names than what the test provides.
3. **Mailpit HTTP API is not reachable.** The step is querying the wrong endpoint.

**Fix:**

1. **Inject both the SMTP host and port separately.** Mailpit exposes two endpoints:
   - HTTP API for querying: `conn::mailpit` (e.g., `http://localhost:8025`)
   - SMTP server for sending: `svc::mailpit-smtp` with separate host and port

   Ensure your SUT gets both:
   ```yaml
   environment:
     services:
       my-app:
         image: myco/my-app:latest
         env:
           SMTP_HOST: ${conn:mail.host}
           SMTP_PORT: ${conn:mail.port}
   ```

2. **Verify the SUT is sending email.** Add a debug HTTP step before the mail assertion to confirm the SUT is processing the request:
   ```yaml
   - id: send-email-request
     type: http.rest
     target: my-app
     method: POST
     path: /send-welcome
     body:
       to: alice@example.com
       subject: "Welcome"
     expect:
       status: 202
   ```

3. **Use `verifyMode: RETRY`** to wait for SMTP delivery:
   ```yaml
   - id: assert-email
     type: mail-expect.smtp
     target: mail
     expect:
       count: 1
       match:
         to: alice@example.com
         subject-contains: "Welcome"
     verifyMode: RETRY
     timeout: 30s
   ```

4. **Check Mailpit is running.** Verify the container is healthy:
   ```bash
   curl http://localhost:8025/api/v1/messages  # Should return JSON
   ```

5. **Understand match semantics.** All criteria (to, subject-contains, body-contains) are AND-conjunctive:
   ```yaml
   match:
     to: alice@example.com
     subject-contains: "Welcome"
     body-contains: "Thank you"
   # Message must match ALL three criteria
   ```

   If you are not finding a message, relax the criteria one by one to isolate the mismatch:
   ```yaml
   # First, check if ANY email exists to this address
   match:
     to: alice@example.com

   # Then add subject filtering
   match:
     to: alice@example.com
     subject-contains: "Welcome"

   # Finally, add body filtering
   match:
     to: alice@example.com
     subject-contains: "Welcome"
     body-contains: "Thank you"
   ```

---

## Cross-scenario state bleed on non-reset stores (DynamoDB, MinIO)

**Symptom:**
When you run multiple scenarios sequentially on the same topology, data from one scenario is visible in the next scenario. This happens with datastores that are not automatically reset (DynamoDB and MinIO).

**What it means:**
DynamoDB and MinIO are not cleared between sequential scenarios — any writes one scenario makes persist into the next. This is a deliberate limitation (out of scope). Other datastores (PostgreSQL, SQL Server, MySQL, MongoDB, Redis, Elasticsearch) are automatically reset between scenarios.

**Solutions:**

1. **Run scenarios in parallel (preferred).** Parallel execution isolates each scenario in its own topology, avoiding any cross-scenario state:
   ```bash
   # Run all scenarios in parallel (each gets its own containers); N must be >= 1
   vouchfx run tests/e2e --parallel 4
   ```

2. **Add explicit cleanup steps.** As the first step of each scenario, explicitly clean the datastore:
   ```yaml
   steps:
     # Scenario 2: start fresh
     - id: cleanup-dynamodb
       type: script.csharp
       description: Clear DynamoDB tables for this scenario.
       code: |
         var config = new Amazon.DynamoDBv2.AmazonDynamoDBConfig { ServiceURL = (string)Vars["conn::dynamo"] };
         var client = new Amazon.DynamoDBv2.AmazonDynamoDBClient(config);
         try
         {
             // Delete items you wrote in the prior scenario (application-specific logic)
         }
         finally { client.Dispose(); }
   ```

   > **Note:** `script.csharp` contributes no compile references of its own — the AWS SDK types
   > above compile only because a step from a provider that contributes them (such as
   > `db-assert.dynamodb`) appears somewhere in the same scenario. A scenario whose only DynamoDB
   > interaction is this cleanup script will fail to compile; include at least one
   > `db-assert.dynamodb` step, or perform the cleanup through your SUT's own API instead.

3. **Use scenario-scoped keys.** Prefix your test data with a scenario identifier, then delete by prefix pattern rather than table-wide flush.

---

## State reset failed (environment error naming a dependency)

**Symptom:**
A scenario fails during the reset phase (between scenarios in a sequential suite) with an environment error message like:
```
state reset (postgres) reset failed for dependency 'orders-db': …
```

**What it means:**
The engine attempted to clear a dependency's state between scenarios (the reset runs after each scenario completes in a sequential multi-scenario suite), but the reset operation failed. This is an **environment error** — a failure of the test infrastructure, not a failure of the system under test. Possible causes:

1. **Store became unhealthy mid-suite** — a container crashed, lost network connectivity, or hit a resource limit. Check Docker logs: `docker logs <container-id>`.
2. **SQL Server with exotic schemas** — temporal (system-versioned) tables are handled, but Respawn may fail on other unusual patterns (cross-database foreign keys, graph tables); consult Respawn's documented limitations.
3. **MongoDB capped or time-series collections** — these collections reject the document deletion the reset performs; remove them from the seeded data or use standard collections.
4. **Elasticsearch reporting per-document failures** — a delete-by-query partially failed; check Elasticsearch logs and ensure the cluster has sufficient resources.
5. **Connection/authentication lost** — the test infrastructure's credentials or network path to the store changed between scenarios.

**Solutions:**

1. **Check the event stream** for the full reset error message — it names the dependency and the specific failure stage (`init`, `create`, or `reset`):
   ```bash
   vouchfx run tests/my-scenario.e2e.yaml --events my-events.jsonl
   # Read the failure details from the environment-error event
   ```

2. **Inspect container health** — if a Docker container reset failed:
   ```bash
   docker ps | grep <dependency-name>
   docker logs <container-id>
   ```

3. **For MongoDB**, remove capped and time-series collections from the test data or switch to standard collections.

4. **For SQL Server**, temporal tables are handled automatically; if the reset still fails, look for other exotic schema patterns (cross-database foreign keys, graph tables) and consult Respawn's documented limitations.

5. **For Elasticsearch**, ensure the cluster is healthy and has sufficient resources; check the cluster health status:
   ```bash
   curl http://localhost:9200/_cluster/health
   ```

---

## Validation errors at authoring time

**Unknown step type (vouchfx validate or pre-compilation)**

**Symptom:**
```
FAIL  my-test.e2e.yaml
    [Schema] (line 45) unknown step type 'db-assert.oracle' — not a registered provider (expected <family>.<provider>, e.g. 'db-assert.postgres').
```

**What it means:**
A step's `type` field is well-formed (matches the `<family>.<provider>` pattern) but does not match any registered provider. This is caught early by `vouchfx validate` (compile-level validation without Docker) with a precise line number and stage, so you can fix the provider name before attempting to run the suite. Note: malformed types like a bare `postgres` or `db_assert.postgres` (with underscore) are rejected by the schema's pattern check instead, reported as separate [Schema] errors — the "unknown step type" message specifically catches well-formed-but-unregistered types.

**Fix:**

1. **Check the step type against the registered providers.** Consult the [Language Reference](language-reference.md) or run `vouchfx list` to see all available step types:
   ```bash
   vouchfx list
   ```

2. **Correct the `type` field to a registered provider.** Ensure it follows the `<family>.<provider>` naming convention and matches one of the Core providers:
   ```yaml
   # Schema error: malformed (missing family)
   - id: my-step
     type: postgres
   
   # Schema error: malformed (wrong separator)
   - id: my-step
     type: db_assert.postgres
   
   # Unknown-type error (well-formed but not a Core provider)
   - id: my-step
     type: db-assert.oracle
   
   # Correct (matches a registered Core provider)
   - id: my-step
     type: db-assert.postgres
   ```

3. **Use `vouchfx validate` before running.** It catches both malformed types (schema pattern errors) and well-formed-but-unregistered types (unknown-step-type errors), plus missing required fields, without needing Docker:
   ```bash
   vouchfx validate ./tests/e2e
   ```

---

## See also

- **[Recipes](recipes.md)** — Task-oriented examples for common scenarios.
- **[vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples)** — Real-world sample applications and test suites demonstrating patterns.
- **[Common Patterns](common-patterns.md)** — Authoring patterns and step structure.
- **[Language Reference](language-reference.md)** — Complete field reference for every step type.
- **[Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md)** — How the system works (Aspire, Roslyn, memory model, verdict taxonomy, secrets).
- **[Project README](project-readme.md)** — Building and running vouchfx, CLI reference, exit codes.
