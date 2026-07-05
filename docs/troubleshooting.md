# vouchfx Troubleshooting

This guide covers real failure modes, what they mean, and how to fix them.

**Quick index:**
- [Docker is not running or not reachable](#docker-is-not-running-or-not-reachable)
- [EnvironmentError: HealthGate timeout of 00:00:20](#environmenterror-healthgate-timeout-of-000020)
- [Discovery root does not exist (dotnet run path resolution gotcha)](#discovery-root-does-not-exist-dotnet-run-path-resolution-gotcha)
- [Understanding the four verdicts](#understanding-the-four-verdicts)
- [Secret leakage in exception messages](#secret-leakage-in-exception-messages)
- [Build fails with warnings-as-errors](#build-fails-with-warnings-as-errors)
- [Aspire topology timeouts or hangs](#aspire-topology-timeouts-or-hangs)
- [Steps run but assertions fail](#steps-run-but-assertions-fail)
- [Capture fails or placeholder is empty](#capture-fails-or-placeholder-is-empty)
- [Kafka messages not consumed (ordering or timing)](#kafka-messages-not-consumed-ordering-or-timing)

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

4. **Serialize topology startup** if you are running tests in parallel locally. The `[assembly: CollectionBehavior(DisableTestParallelization=true)]` attribute (on xUnit test projects that use Aspire) serialises test startup, preventing concurrent DCP resource startup from overwhelming the machine and triggering timeouts. This is already applied in the vouchfx test projects (see `Platform.Engine.Runtime.Tests` for the example).

5. **In CI, increase runner capacity or use a faster image pull network.** If your CI runner has constrained network or disk bandwidth, image pulls take longer. Use a faster runner if available, or pre-cache the image in the runner's local Docker registry.

**Why this happens:** Aspire's Distributed Cloud Provisioning (DCP) watches each resource and gives it ~20 seconds to become healthy. This is a safety net to prevent tests from hanging indefinitely. If multiple resources start concurrently, they compete for I/O (disk, network), and a large image pull can exceed 20 seconds. Pre-warming the cache is the most reliable fix.

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
`dotnet run` changes the working directory to the **project directory** (`src/Cli/Vouchfx.Cli/`), not your current directory. So a relative path like `./tests/e2e` resolves relative to that project directory, not your repository root, and fails.

**Fix:**

Use one of these approaches:

1. **Run the built binary directly** (recommended):
   ```bash
   src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx run ./tests/e2e
   ```
   (On Windows: `src\Cli\Vouchfx.Cli\bin\Release\net8.0\vouchfx.exe run .\tests\e2e`)

   The binary runs from your current directory, so relative paths work as expected.

2. **Pass an absolute path to `dotnet run`:**
   ```bash
   dotnet run --project src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -- run $(pwd)/tests/e2e
   ```
   (On Windows: `dotnet run --project src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -- run $((Get-Location).Path)/tests/e2e`)

**Best practice:** For local development, build the CLI once and run the binary directly. This is faster (skips compilation) and avoids path-resolution surprises.

---

## Understanding the four verdicts

vouchfx distinguishes four outcomes (see `docs/01` §12.1 for the full taxonomy):

| Verdict | Meaning | Example | Default CI exit code |
|---------|---------|---------|----------------------|
| **Pass** | All assertions passed. | A test runs end-to-end and all steps succeed. | 0 (success) |
| **Fail** | An assertion failed — a genuine product defect. | `expect: { status: 200 }` but the API returned 500. | 1 (always breaks CI) |
| **EnvironmentError** | Infrastructure problem, not a product defect. | Docker daemon unreachable, image pull fails, seed SQL fails. | 0 by default; 3 if `--fail-on-env-error` |
| **Inconclusive** | The engine could not decide; the assertion may pass if retried. | A RETRY step's polling window expires; a capture expression fails to match. | 0 by default; 4 if `--fail-on-inconclusive` |

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

## See also

- **[Recipes](recipes.md)** — Task-oriented examples for common scenarios.
- **[vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples)** — Real-world sample applications and test suites demonstrating patterns.
- **[Common Patterns](common-patterns.md)** — Authoring patterns and step structure.
- **[Language Reference](language-reference.md)** — Complete field reference for every step type.
- **[Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md)** — How the system works (Aspire, Roslyn, memory model, verdict taxonomy, secrets).
- **[README.md](../README.md)** — Building and running vouchfx, CLI reference, exit codes.
