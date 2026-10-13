# HTTP and PostgreSQL with Compose

This application is independent of the engine's inline dependency catalogue. Compose builds it, and explicit bindings expose `api` and `db` to the ordinary HTTP/PostgreSQL assertions. Each scenario gets a fresh owned project. The first assertion proves the database starts empty; the HTTP request creates an order; the final assertion proves it was stored.

Core `1.0.0` introduced support for this suite's `environment.source`; legacy `1.0.0-rc.6` does not accept it. From the repository root, with the .NET 8 SDK and Docker running Linux containers, install the latest stable CLI and check the Docker Compose plugin:

```powershell
$env:VOUCHFX_COMPOSE_PASSWORD = 'choose-a-local-test-password'
dotnet tool install --global vouchfx
vouchfx --version
docker compose version
vouchfx validate examples/compose-orders/compose.e2e.yaml
vouchfx run examples/compose-orders/compose.e2e.yaml --fail-on-env-error --fail-on-inconclusive
```

Bash uses `export VOUCHFX_COMPOSE_PASSWORD='choose-a-local-test-password'` followed by the same `dotnet`, `vouchfx` and `docker compose` commands. Already installed? Use `dotnet tool update --global vouchfx`. Validation reads configuration and manifests without invoking Docker or resolving the password. Running builds the application, discovers random IPv4 ports, probes HTTP/PostgreSQL readiness, executes three steps and removes owned containers, networks and volumes. Require `pass=3`, with zero fail/environment-error/inconclusive steps; exit zero alone is insufficient. The built image remains in Docker's cache.

For contributor work on unreleased source, build `dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release -warnaserror`, then replace `vouchfx` in the commands with `dotnet src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll` to select that checkout explicitly.

After the Compose run has built the local image, run the identical assertion steps with inline provisioning. This comparison uses Aspire and also needs the selected CLI's [version-specific DCP prerequisites](../../docs/getting-started.md#installing-vouchfx): source AppHost builds use their restore/metadata fallback, while stable installed distributions carry matching native tools.

```sh
vouchfx run examples/compose-orders/inline.e2e.yaml --fail-on-env-error --fail-on-inconclusive
```

Use `vouchfx run examples/compose-orders/compose.e2e.yaml --watch` to recreate the project on each saved suite or environment input. Config, manifests and env files are watched. This example intentionally uses no shared resources, fixed container names, fixed host ports, seed or TLS declarations. See [the Compose guide](../../docs/compose-environments.md) for limitations and interrupted-run recovery.
