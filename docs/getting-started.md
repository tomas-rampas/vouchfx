# Getting Started with vouchfx

**Welcome to vouchfx** — a declarative testing platform for distributed systems.

This 60-minute guide walks you through your first end-to-end integration test: from checking that your environment is ready, through building vouchfx from source, authoring a minimal `.e2e.yaml` test file, running it, and interpreting the verdict. By the end, you will have a PASS and a working understanding of how the platform works.

## What is vouchfx?

vouchfx tests **distributed systems end-to-end** by letting you author tests in declarative YAML. A test describes a system under test (REST services, databases, message brokers in Docker containers), then orchestrates their topology, runs ordered steps (HTTP calls, database assertions, scripts) against discovered endpoints, and reports a clear verdict: **Pass**, **Fail**, **Environment error** (infrastructure breakage), or **Inconclusive** (timeout / unmet condition).

The pipeline is: `.e2e.yaml` → validate vs JSON Schema → compile YAML→AST→C#→Roslyn (once) → orchestrate containers via Aspire/Testcontainers → execute steps → collect verdict → unload & reset.

This guide focuses on the essentials. For architecture, the provider model, secrets, and advanced features, see the [Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md).

## Prerequisites

**What you need on your machine.**

### .NET 8 SDK

vouchfx targets **.NET 8 LTS**. Check your SDK version:

```bash
dotnet --version
```

You should see `8.0.x` or later. If not, [install .NET 8.0 LTS](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

### Docker daemon

vouchfx orchestrates containerised topologies using Docker. The engine starts containers, health-gates them, and cleans them up. Verify Docker is running:

```bash
docker info
```

You should see version and runtime information. On Windows, this typically means [Docker Desktop](https://www.docker.com/products/docker-desktop/) with the Linux engine running; on macOS, Docker Desktop likewise; on Linux, a running `dockerd`.

## Building vouchfx from source

vouchfx is not yet a published `.NET Tool` (that lands in Sprint 11). For now, you build it from source. Clone the repository if you haven't already:

```bash
git clone https://github.com/vouchfx-org/vouchfx.git
cd vouchfx
```

Build the entire solution:

```bash
dotnet build vouchfx.sln
```

This compiles the engine, providers, and the CLI runner. The build takes a minute or two and should produce zero warnings.

Alternatively, if you want only the CLI (faster), build that project:

```bash
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release
```

## Authoring your first test

### The structure of a `.e2e.yaml` file

A test file has four top-level sections; only `steps` is mandatory:

- **`metadata`** — name, owner, tags, description. Used for reporting and filtering; no execution effect.
- **`environment`** — `services` (containers under test), `dependencies` (managed Aspire resources like databases), optional `seed` (SQL/fixtures applied after topology is healthy).
- **`variables`** — optional constants pre-loaded into the shared context, available to all steps.
- **`steps`** — the ordered list of actions and assertions (HTTP calls, database queries, scripts, listeners).

### The worked example

Here is a minimal, complete, runnable test. Copy this into a new file `hello-world.e2e.yaml` in your project:

```yaml
metadata:
  name: getting-started-hello-world
  owner: vouchfx-newcomers
  tags: [getting-started, hello-world]
  description: >-
    The first vouchfx test — a single http.rest GET against traefik/whoami.
    Asserts the response is HTTP 200 and captures the container hostname
    via JSONPath for demonstration.

environment:
  services:
    whoami:
      image: traefik/whoami
      httpPort: 80

steps:
  - id: whoami-GET-api
    type: http.rest
    target: whoami
    method: GET
    path: /api
    expect:
      status: 200
    capture:
      hostname: "$.hostname"
```

**What each part does:**

| Field | Role |
|-------|------|
| `metadata.name` | Human-readable test name for reports. |
| `metadata.owner` | Team or person responsible; useful for filtering large suites. |
| `metadata.tags` | Labels to select subsets of tests (e.g., `--tag smoke`). |
| `environment.services.whoami` | A named service under test. The `image:` must be a valid OCI image (defaults to Docker Hub). |
| `httpPort: 80` | Tells vouchfx the container listens on port 80; the engine health-gates this port before running steps. |
| `steps[0].id` | Unique step identifier. Used in reporting and for referencing this step's captures in later steps. |
| `type: http.rest` | The step family and provider: make an HTTP request. |
| `target: whoami` | Which service to call (matches a name in `environment.services`). |
| `method: GET` | HTTP verb (GET, POST, PUT, DELETE, etc.). |
| `path: /api` | The URL path. The full URL is built as `http://<target>:<httpPort><path>`. |
| `expect.status: 200` | Assert the response is HTTP 200. If the status code differs, the step fails. |
| `capture.hostname: "$.hostname"` | JSONPath expression. Extracts the `hostname` field from the JSON response body and stores it in `Vars["hostname"]`. Any later step can reference this as `{hostname}`. |

The `traefik/whoami` container is a tiny utility that responds to any HTTP request with a JSON object describing itself, including a `hostname` field. This makes it a perfect pedagogical system under test: it is guaranteed to respond, requires no configuration, and the response contains extractable data.

**Where to save your test file:**

You can save it anywhere in your project tree. For example:

- `examples/getting-started/hello-world.e2e.yaml` (in the vouchfx repo itself, as shipped)
- `tests/e2e/hello-world.e2e.yaml` (a typical project layout)
- Any directory with a `.e2e.yaml` extension is discoverable by the `vouchfx` CLI

## Running your test

Once you have authored `hello-world.e2e.yaml`, build the CLI and run it from the repository root. The binary resolves scenario paths relative to your current directory, so you must invoke it directly (not via `dotnet run`, which changes the working directory).

**Build the CLI once:**

```bash
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release
```

**Run the example:**

```bash
src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx run examples/getting-started
```

(On Windows the binary is `vouchfx.exe`, so the command is `…\net8.0\vouchfx.exe run examples\getting-started`.)

This command:

1. Discovers all `.e2e.yaml` files in `examples/getting-started/`.
2. Validates each file against the JSON Schema.
3. For each scenario, starts the container topology (the `whoami` service), health-gates it, runs the steps in order, and collects verdicts.
4. Renders a terminal report with per-step verdicts.

**Tip:** Add the `bin/Release/net8.0` directory to your PATH (or create an alias `vouchfx`) so you can type just `vouchfx run <path>` — exactly how the packaged tool will work in Sprint 11. Alternatively, if you prefer to use `dotnet run`, you must pass an **absolute** scenario path (because `dotnet run` executes from the project directory):

```bash
dotnet run --project src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -- run "$(pwd)/examples/getting-started"
```

**What a successful run looks like:**

You should see output similar to:

```
Scenario 'getting-started-hello-world' started
  step 'whoami-GET-api' started
  step 'whoami-GET-api': PASS (19 ms)
    provenance:
      captured 'hostname' <- step 'whoami-GET-api' ($.hostname)
Scenario 'getting-started-hello-world': PASS  (pass=1 fail=0 envError=0 inconclusive=0)
```

(Before the scenario line, you may see Aspire/topology startup messages such as "Distributed application started.")

On an interactive terminal, the verdict is colour-coded and prefixed with a shape glyph: `[+]` for PASS, `[x]` for FAIL, `[!]` for ENV_ERROR, or `[?]` for INCONCLUSIVE. When output is piped (as in CI), glyphs and colours are omitted and you see plain text as shown above. The process exit code (0 on Pass) is the shell's `$?`, not a printed line.

**If something goes wrong:**

- **ENV_ERROR — HealthGate timeout:** the `whoami` container failed to become healthy. Check that Docker is running and can pull `traefik/whoami`. Run `docker pull traefik/whoami:latest` to warm the cache. Note that a cold image pull can trip a shorter internal DCP per-resource startup window (120 seconds is our outer gate, but DCP has internal timeouts — so pre-pulling and warming images helps on first run).
- **FAIL — assertion error:** a step assertion failed (e.g., the HTTP status was 500, not 200). You will see a diff of expected vs. actual beneath the step line. Verify the service is running and the path `/api` is correct.
- **INCONCLUSIVE — timeout or unmet condition:** the step took longer than its timeout, or a capture expression did not match the response. Unlikely on localhost; more common with slow networks or overloaded machines.

## The four verdicts

vouchfx keeps four outcomes distinct everywhere (reporting, exit codes, CI gating):

| Verdict | Meaning | Breaks CI by default? |
|---------|---------|---|
| **Pass** | The scenario and all steps succeeded. | No |
| **Fail** | A step assertion failed (e.g., status was 500, not 200). This is a product defect. | **Yes** |
| **Environment error** | Infrastructure broke (container unhealthy, image pull failed, seed failed). | No |
| **Inconclusive** | The engine could not decide (timeout, unmet capture, partition outlasted grace period). | No |

This distinction is crucial: a `Fail` means your code is broken; an `EnvironmentError` means your CI infrastructure is broken. By default, only `Fail` breaks the build — allowing you to distinguish product defects from infrastructure problems. You can opt in to gating on the others with `--fail-on-env-error` and `--fail-on-inconclusive`.

## Generating reports

By default, `vouchfx run` prints a plain-text terminal report. You can also emit:

### HTML report

A self-contained, interactive HTML file with per-step details, captured variables, and the reproducibility envelope (no secret values embedded):

```bash
src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx run examples/getting-started --html report.html
```

Open `report.html` in your browser to see a detailed timeline of each step, captured values, and any failures.

### JUnit XML

For CI integration (GitHub Actions, GitLab, Jenkins):

```bash
src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx run examples/getting-started --junit results.xml
```

The XML file maps vouchfx verdicts to standard JUnit elements (`<failure>` for Fail, `<error>` for EnvironmentError, `<skipped>` for Inconclusive), so your CI system can ingest and visualise results natively.

### Both together

```bash
src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx run examples/getting-started --html report.html --junit results.xml
```

## Next steps

Now that you have a passing test, here's where to go:

### Recipes & cookbook

The `docs/recipes.md` file collects common testing patterns: capturing and reusing values across steps, waiting for eventual consistency with `verifyMode: RETRY`, seeding databases, calling webhooks, and publishing/consuming Kafka events. Also see `docs/common-patterns.md` for authoring patterns and `docs/troubleshooting.md` for failure modes and fixes.

### Full DSL specification

For step types beyond `http.rest` (database assertions, scripts, message queues, webhooks), see [`docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md`](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md). It covers:

- Every step family: `http.rest`, `db-assert.postgres`, `script.csharp`, `mq-publish.kafka`, `mq-expect.kafka`, `webhook-listen.http`.
- Capture and placeholder syntax: threading state forward.
- Verifymode: `IMMEDIATE` (assert now) vs. `RETRY` (engine-owned polling with backoff).
- Secrets: reference-only syntax (`${secret:env/NAME}` / `${secret:vault/path}`), resolved at execution time.
- Seed: SQL fixtures and warm-up logic applied before the first step.

### Architecture Blueprint

For deep understanding of how the platform is built, memory safety, orchestration, and the provider model, read [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](01_Technical_Architecture_and_Engineering_Blueprint.md).

### CI integration

To run vouchfx tests in GitHub Actions or GitLab CI/CD, see the [README](../README.md) under "CI integration with GitHub Actions" and "CI integration with GitLab CI". The reusable workflows handle building, running, and publishing reports.

### Writing a custom provider

Once you're comfortable with the built-in steps, you can write your own. See [`CONTRIBUTING.md`](../CONTRIBUTING.md) and the [`examples/Example.Steps.Hello`](../examples/Example.Steps.Hello) template provider for the contract and a worked example.

## Summary

You now have:

1. ✓ Verified the .NET 8 SDK and Docker daemon are installed.
2. ✓ Built vouchfx from source.
3. ✓ Authored a minimal `.e2e.yaml` test file.
4. ✓ Run the test and seen a **PASS** verdict.
5. ✓ Generated HTML and JUnit reports.
6. ✓ Learned the four verdicts and their CI implications.
7. ✓ Discovered where to find recipes, the full DSL, and architecture docs.

**The next hour?** Pick a real service from your own codebase, author a test against it, and explore the other step types (database assertions, scripts, message queues). Start with `http.rest` and capture as here; as you grow the test, the DSL spec will guide you through the rest.

Welcome to distributed system testing that you can read and reason about. Happy testing.
