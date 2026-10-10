# HTTP and PostgreSQL with Compose

This application is independent of the engine's inline dependency catalogue. Compose builds it, and explicit bindings expose `api` and `db` to the ordinary HTTP/PostgreSQL assertions. Each scenario gets a fresh owned project. The first assertion proves the database starts empty; the HTTP request creates an order; the final assertion proves it was stored.

Use this compatible engine source checkout or an explicitly selected Compose-capable installed candidate. Published `1.0.0-rc.6` does not accept this suite's `environment.source`. From the repository root, with the .NET 8 SDK and Docker running Linux containers, build and select the source CLI explicitly and check the Docker Compose plugin:

```powershell
$env:VOUCHFX_COMPOSE_PASSWORD = 'choose-a-local-test-password'
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release -warnaserror
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll --version
docker compose version
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll validate examples/compose-orders/compose.e2e.yaml
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll run examples/compose-orders/compose.e2e.yaml --fail-on-env-error --fail-on-inconclusive
```

Bash uses `export VOUCHFX_COMPOSE_PASSWORD='choose-a-local-test-password'` followed by the same `dotnet` and `docker compose` commands. Validation reads configuration and manifests without invoking Docker or resolving the password. Running builds the application, discovers random IPv4 ports, probes HTTP/PostgreSQL readiness, executes three steps and removes owned containers, networks and volumes. Require `pass=3`, with zero fail/environment-error/inconclusive steps; exit zero alone is insufficient. The built image remains in Docker's cache.

After the Compose run has built the local image, run the identical assertion steps with inline provisioning. This comparison uses Aspire and also needs the selected CLI's [version-specific DCP prerequisites](../../docs/getting-started.md#installing-vouchfx): source AppHost builds use their restore/metadata fallback, while bundled installed candidates carry matching native tools.

```sh
dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll run examples/compose-orders/inline.e2e.yaml --fail-on-env-error --fail-on-inconclusive
```

Use `dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll run examples/compose-orders/compose.e2e.yaml --watch` to recreate the project on each saved suite or environment input. Config, manifests and env files are watched. This example intentionally uses no shared resources, fixed container names, fixed host ports, seed or TLS declarations. See [the Compose guide](../../docs/compose-environments.md) for limitations and interrupted-run recovery.
