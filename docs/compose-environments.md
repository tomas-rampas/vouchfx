# Owned Docker Compose environments

Use your own Compose project with existing vouchfx assertions. Select a registered environment driver in the suite; the adapter owns its separate configuration grammar. Omitting `source` retains inline provisioning. Core `1.0.0` introduced Compose support through the engine's binding/ready-lease boundary. That contract is format-neutral; other external formats require a registered driver implementation without changing step providers.

Install the latest stable CLI with the .NET 8 SDK and verify the native Docker Compose plugin:

```sh
dotnet tool install --global vouchfx
vouchfx --version
docker compose version
```

Already installed? Use `dotnet tool update --global vouchfx`. Execution requires Docker running Linux containers on a supported local daemon; validation alone does not require Docker. Legacy `1.0.0-rc.6` does not accept `environment.source`; upgrade before using this guide.

For contributor work on unreleased source, build and select that checkout's DLL explicitly:

```sh
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release -warnaserror
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll --version
```

```yaml
environment:
  source:
    driver: compose
    config: ./environment.compose.yaml
```

The configuration path is relative to the suite. It cannot be combined with inline services, dependencies, seed or image defaults.

## Build your backend from a Dockerfile

Declare the build in your native Compose manifest. For example, with `compose.yaml`
beside `environment.compose.yaml`, and an `app/` directory beside both:

```yaml
# compose.yaml
services:
  web:
    build:
      context: ./app
      dockerfile: Dockerfile.e2e
    ports:
      - target: 8080
        host_ip: 127.0.0.1
```

Here Compose builds `app/Dockerfile.e2e` with `app/` as its build context. The
`dockerfile` path is relative to that context; omit it to use the usual
`app/Dockerfile`. The application must listen on port 8080 and expose the health
path selected in the binding below. Vouchfx starts the owned project with native
`docker compose up --detach --build`, then checks the declared endpoints before
running assertions. The build remains subject to the startup deadline.

The suite selects the Compose configuration, rather than a Dockerfile directly.
There is no inline `dockerfile` or `build` field under `environment.services`.
For an inline environment, build the image first:

```sh
docker build -t my-backend:e2e -f ./app/Dockerfile.e2e ./app
```

```yaml
environment:
  services:
    api:
      image: my-backend:e2e
      httpPort: 8080
```

Run that suite against the same Docker daemon that holds the built image. Inline
provisioning starts the image; it does not build the Dockerfile.

## Bind logical targets

Name the HTTP, TCP and PostgreSQL targets your assertions use. Manifest/env-file paths are relative to this configuration; native Compose handles merging, interpolation and project/build/mount paths.

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
  broker:
    type: tcp
    service: kafka
    port: 9092
    readiness: {type: healthcheck}
dependencies:
  db:
    type: postgres
    service: postgres
    port: 5432
    database: app
    username: app
    password: "${secret:env/TEST_PASSWORD}"
```

`files` is an ordered, nonempty list. `projectDirectory` defaults to the first manifest's directory. Optional `envFiles` selects Compose interpolation files; service `env_file` inputs are tracked too. `environment` accepts literal values or whole environment-secret references. Its names start with an ASCII letter or underscore, followed by ASCII letters, digits, underscores or dots, preserving case; `VOUCHFX_PROBE.key_1` is valid. Malformed names are refused without printing them. `COMPOSE_` and `DOCKER_` names remain reserved for ownership and daemon selection. This configuration rule does not reinterpret native manifest `environment` fields. Passwords require `${secret:env/NAME}`. Secrets resolve only during acquisition through the run's scrub ledger. Unknown configuration keys are refused.

Publish exactly one IPv4 TCP mapping for each selected service/port, with one replica. Random host ports are supported; fixed host ports are retained and conflicts fail startup. IPv6-only and multiple mappings are refused. A published `0.0.0.0` address is reached through host loopback.

| Binding | Readiness | Value supplied to assertions |
| --- | --- | --- |
| HTTP, the default; optional `type: http` | `readiness: {path: /health, status: 200}`; optional readiness `type: http` | `svc::<name>` holds `http://host:port` |
| `type: tcp` service | `readiness: {type: tcp}` proves host-port reachability only | `svc::<name>` holds bare `host:port` |
| `type: tcp` service | `readiness: {type: healthcheck}` requires its native healthcheck to be healthy and its host port reachable | `svc::<name>` holds bare `host:port` |
| `type: postgres` dependency | Opens the declared database and executes `SELECT 1` | `conn::<name>` holds its connection string |

TCP readiness must name its mode explicitly. Use `healthcheck` for brokers that need a protocol-level readiness command: configure an enabled native Compose `healthcheck.test` for that service. The adapter refuses missing, disabled or `NONE` checks before starting resources; an image's implicit healthcheck cannot satisfy this authoring requirement. At runtime it accepts healthy state only from the exact owned project's service container. Healthcheck command output is suppressed and is never stored in the ownership journal. A reachable TCP port alone does not prove broker readiness.

Existing Kafka publish/expect steps can target the logical `broker` service. Supply a host-reachable Kafka advertised listener matching the selected published port; the adapter does not rewrite Kafka advertisements. Application-to-broker traffic can use a separate listener on Compose DNS. Use an isolated fixed listener port when the broker's advertisement requires it; conflicts fail startup. Configuration, binding and readiness remain adapter concerns, so another registered format can supply the same normalised service value.

Kafka retained-record assertions manually assign every actual topic partition at its earliest offset; they do not wait for a fresh consumer group to rebalance. Metadata discovery and consumption share the existing one-second attempt budget. An empty partition does not end the drain while another partition remains unread. Use `verifyMode: RETRY` for messages that have not arrived yet; a step timeout does not widen an individual drain. Native broker health and HTTP success still require the separate Kafka business assertion.

## Validate, run and watch

```sh
vouchfx validate ./orders.e2e.yaml
vouchfx run ./orders.e2e.yaml --fail-on-env-error --fail-on-inconclusive --events ./events.jsonl
vouchfx run ./orders.e2e.yaml --watch
vouchfx schema --environment-driver compose --output ./compose-schema.json
```

Validation is pure: it reads authoring files and compiles normalised targets without running subprocesses, resolving credentials or creating containers. Runtime uses the same declarations. Config, manifest and env-file edits invalidate the generic watch plan and trigger another run. Every Compose scenario/watch execution acquires a fresh project; logical dependency names remain available for `conn::` staging even though reset/reseed is prohibited.

Use both failure flags in CI and require the expected passing step counts. Environment errors and runtime inconclusive results otherwise retain the existing opt-in exit policy; exit zero alone does not prove the suite passed.

The VS Code extension associates `*.compose.yaml` with the separate configuration schema. The ordinary suite schema remains for `*.e2e.yaml`. `vouchfx plan` explicitly reports external-source suites as unanalysable; it does not claim an empty inline topology was analysed. `scaffold` supports inline outlines and refuses source intents; author the source/config explicitly. Downstream schema-only tools cannot confirm external bindings without running core validation.

## Ownership and recovery

Each acquisition generates a random project identity on the selected local Docker daemon. Native Compose renders and validates the merged model before mutation. A native stdin reload must preserve its JSON configuration, including literal dollars in credentials; incompatible Compose versions are refused before startup. Startup/discovery consume that exact snapshot in memory. Rendered credentials and env-file contents are never journalled or included in subprocess diagnostics.

Cleanup uses project-labelled Docker resource inventory, rechecks each resource's ownership and removes only its exact ID/name. It does not reread edited manifests or run global prune. Cleanup has an independent deadline and is idempotent. A cleanup failure is reported while retaining the primary scenario result and its recovery journal. Built/pulled images remain cached.

Hard termination cannot run `finally`. Recovery records live under `%LOCALAPPDATA%\vouchfx\environments` on Windows, or the platform's local application-data directory. Recover one interrupted project:

```sh
vouchfx environment cleanup --journal /path/to/vouchfx/environments/vouchfx-PROJECT_ID.json
```

Recovery requires access to the recorded local daemon, not original manifests or credentials. Successful cleanup removes the journal; failed or timed-out cleanup retains it.

## Current limits

This driver supports owned HTTP/TCP/PostgreSQL environments with IPv4 published endpoints on local Unix-socket or Windows named-pipe Docker daemons. Explicit `DOCKER_HOST` values must use a literal absolute Unix socket path, such as `unix:///var/run/docker.sock`, or `npipe:////./pipe/<name>` with one nonempty pipe name. Unix socket URIs cannot contain an authority, query, fragment, encoding, dot segments or empty path segments. Use isolated test overrides. External storage/networks, explicit shared resource names, fixed container names, delegated Compose provider plugins, custom resource drivers/options, reserved Compose labels, writable host mounts, host namespaces/devices/capabilities, explicit `uts`/`userns_mode`/`cgroup` settings and `use_api_socket` are refused before startup. Active `include`/`extends`, seed, borrowed resources, reuse and external-driver TLS/mTLS declarations are unsupported. Existing inline security validation remains available. These ownership checks do not sandbox user container images; review read-only host mounts and application behaviour yourself.

In this matching source checkout, follow `examples/compose-orders/README.md` for the HTTP mutation and database example, including its identical inline assertion steps.

The [shared-app .NET Compose orders sample](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/orders-dotnet-compose) builds its application from the shared Dockerfile and demonstrates all five business assertions: REST creation, the PostgreSQL row, the retained Kafka event, the outbound webhook and the final REST refetch. A Windows run with Linux Docker containers on 25 October 2026 passed all five assertions and exited 0 using the official Core 1.2.1 GitHub Release package. Its README describes the pinned images, native readiness checks and standard sample runner. Compose support predates that release; 1.2.1 also corrects GitLab CI exit handling.

## Contributor acceptance coverage

The Linux Docker CI lane runs these ownership cases in the exclusive Compose acceptance collection. It retains first-attempt JSON evidence and the five cold CLI event/output files in the `owned-environment-acceptance` artefact, including partial records when a case fails. Its file-only admission gate requires all three cases and exact completed counts; a green exit without these records is insufficient.

| Capability | Native regression case |
| --- | --- |
| Requested profile starts its service; another profile stays inactive | `Compose_RequestedProfileAndOrderedInterpolationFilesAndServiceEnvFile_AffectRealContainer` |
| Later interpolation env-file wins; service `env_file` reaches its container independently | The same capability case inspects the actual hostname and container environment |
| Ordered manifest override/build, credentials and published HTTP/PostgreSQL targets | `NormalWatch_ConfigEditOutsideSuite_TwoFreshPassingRuns_CleansOwnedResources` |
| Fixed-port conflict and readiness deadline refuse a passing result | `TwoNormalScenarios_AreFresh_FixedPortConflictAndReadinessDeadlineCannotPass` |
| Assertion failure and cancellation release only owned resources | `NormalRun_AssertionFailure_AndCancellationDuringReadiness_CleanOnlyOwnedResources` |
| Repeated fresh state and topology release | `TwentyWarmAcquisitions_TenInlineAndTenCompose_ReuseOneCompiledImageAndReleaseFreshState` |
| Fresh CLI/compiler process and writable profile | `FiveColdCliProcesses_ThreeInlineAndTwoCompose_FreshWritableProfilesAndOwnedClosure` |

Warm means one test-host process and one emitted PostgreSQL assertion image across 20 fresh acquisitions, alternating ten inline and ten Compose environments. Each cycle proves an initially absent table, writes state, observes it through the compiled provider, and requires container/network/volume, journal and DCP release before continuing. Foreign resource IDs/creation times and native process birth identities must remain unchanged.

Cold means five distinct CLI process birth identities with separate writable app-data, temporary and .NET/NuGet profile directories on Linux, covering three inline and two Compose runs. Each CLI must produce two passing steps, one passing scenario and exit zero; Compose must actually journal within its isolated profile, and inline acquisition must observe its DCP host. These are bounded regression samples with existing image layers and build artefacts retained. They do not measure uncached pulls, certify every OS, or replace the separate TLS/mTLS acceptance lane. Compose reset/reseed remains unsupported; fresh state is established by recreation.

Run the focused cases on an isolated Linux Docker host:

```sh
dotnet test tests/Vouchfx.Cli.Tests -c Release --filter "requires=docker&FullyQualifiedName~EnvironmentTopologyDurabilityDockerTests"
```

The format-neutral driver boundary is unchanged. These cases exercise the built-in Compose adapter and inline provisioning; other formats still require their own registered driver.
