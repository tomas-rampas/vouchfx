# Owned Docker Compose environments

Use your own Compose project with existing vouchfx assertions. Select a registered environment driver in the suite; the adapter owns its separate configuration grammar. Omitting `source` retains inline provisioning. Current source supports Compose through the engine's binding/ready-lease boundary, which also permits other registered drivers without changing step providers. Published `1.0.0-rc.6` does not support `environment.source`.

Use a CLI built from this compatible source checkout or an explicitly selected Compose-capable installed candidate. For the commands below, work from the repository root, build the Release CLI with the .NET 8 SDK, and verify the native Docker Compose plugin:

```sh
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release -warnaserror
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll --version
docker compose version
```

These commands select this checkout's built DLL explicitly. Execution requires Docker running Linux containers on a supported local daemon; validation alone does not require Docker. An installed candidate must declare its version and prerequisites; do not substitute the published rc.6 global tool.

```yaml
environment:
  source:
    driver: compose
    config: ./environment.compose.yaml
```

The configuration path is relative to the suite. It cannot be combined with inline services, dependencies, seed or image defaults.

## Bind logical targets

Name the HTTP and PostgreSQL targets your assertions use. Manifest/env-file paths are relative to this configuration; native Compose handles merging, interpolation and project/build/mount paths.

```yaml
version: 1
files: [./compose.yaml, ./compose.test.yaml]
projectDirectory: .
profiles: [test]
environment:
  TEST_PASSWORD: "${secret:env/TEST_PASSWORD}"
startupTimeoutSeconds: 120
cleanupTimeoutSeconds: 30
services:
  api:
    service: web
    port: 8080
    readiness: {path: /health, status: 200}
dependencies:
  db:
    type: postgres
    service: postgres
    port: 5432
    database: app
    username: app
    password: "${secret:env/TEST_PASSWORD}"
```

`files` is an ordered, nonempty list. `projectDirectory` defaults to the first manifest's directory. Optional `envFiles` selects Compose interpolation files; service `env_file` inputs are tracked too. `environment` accepts literal values or whole environment-secret references. Passwords require `${secret:env/NAME}`. Secrets resolve only during acquisition through the run's scrub ledger. Unknown configuration keys are refused.

Publish exactly one IPv4 TCP mapping for each selected service/port. Random host ports are supported; fixed host ports are retained and conflicts fail startup. IPv6-only and multiple mappings are refused. HTTP readiness requires the selected status from the specified path. PostgreSQL readiness opens the declared database and executes `SELECT 1`; a running container alone cannot satisfy readiness.

## Validate, run and watch

```sh
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll validate ./orders.e2e.yaml
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll run ./orders.e2e.yaml --fail-on-env-error --fail-on-inconclusive --events ./events.jsonl
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll run ./orders.e2e.yaml --watch
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll schema --environment-driver compose --output ./compose-schema.json
```

Validation is pure: it reads authoring files and compiles normalised targets without running subprocesses, resolving credentials or creating containers. Runtime uses the same declarations. Config, manifest and env-file edits invalidate the generic watch plan and trigger another run. Every Compose scenario/watch execution acquires a fresh project; logical dependency names remain available for `conn::` staging even though reset/reseed is prohibited.

Use both failure flags in CI and require the expected passing step counts. Environment errors and runtime inconclusive results otherwise retain the existing opt-in exit policy; exit zero alone does not prove the suite passed.

The VS Code extension associates `*.compose.yaml` with the separate configuration schema. The ordinary suite schema remains for `*.e2e.yaml`. `vouchfx plan` explicitly reports external-source suites as unanalysable; it does not claim an empty inline topology was analysed. `scaffold` supports inline outlines and refuses source intents; author the source/config explicitly. Downstream schema-only tools cannot confirm external bindings without running core validation.

## Ownership and recovery

Each acquisition generates a random project identity on the selected local Docker daemon. Native Compose renders and validates the merged model before mutation. A native stdin reload must preserve its JSON configuration, including literal dollars in credentials; incompatible Compose versions are refused before startup. Startup/discovery consume that exact snapshot in memory. Rendered credentials and env-file contents are never journalled or included in subprocess diagnostics.

Cleanup uses project-labelled Docker resource inventory, rechecks each resource's ownership and removes only its exact ID/name. It does not reread edited manifests or run global prune. Cleanup has an independent deadline and is idempotent. A cleanup failure is reported while retaining the primary scenario result and its recovery journal. Built/pulled images remain cached.

Hard termination cannot run `finally`. Recovery records live under `%LOCALAPPDATA%\vouchfx\environments` on Windows, or the platform's local application-data directory. Recover one interrupted project:

```sh
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll environment cleanup --journal /path/to/vouchfx/environments/vouchfx-PROJECT_ID.json
```

Recovery requires access to the recorded local daemon, not original manifests or credentials. Successful cleanup removes the journal; failed or timed-out cleanup retains it.

## Current limits

This driver supports owned HTTP/PostgreSQL environments on local Unix-socket or Windows named-pipe Docker daemons. Explicit `DOCKER_HOST` values must use a literal absolute Unix socket path, such as `unix:///var/run/docker.sock`, or `npipe:////./pipe/<name>` with one nonempty pipe name. Unix socket URIs cannot contain an authority, query, fragment, encoding, dot segments or empty path segments. Use isolated test overrides. External storage/networks, explicit shared resource names, fixed container names, delegated Compose provider plugins, custom resource drivers/options, reserved Compose labels, writable host mounts, host namespaces/devices/capabilities, explicit `uts`/`userns_mode`/`cgroup` settings and `use_api_socket` are refused before startup. Active `include`/`extends`, seed, borrowed resources, reuse and external-driver TLS/mTLS declarations are unsupported. Existing inline security validation remains available. These ownership checks do not sandbox user container images; review read-only host mounts and application behaviour yourself.

In this matching source checkout, follow `examples/compose-orders/README.md` for the HTTP mutation and database example, including its identical inline assertion steps.
