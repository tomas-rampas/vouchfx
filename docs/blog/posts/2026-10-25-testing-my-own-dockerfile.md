---
date: 2026-10-25
authors:
  - tomas-rampas
categories:
  - Releases
---

# Testing my own Dockerfile with vouchfx

I released Core 1.2.1 and MCP 1.1.0, then ran the samples again. This included building the backend from my own Dockerfile, with Docker Compose describing the application and the services around it.

<!-- more -->

Compose support has been available since Core 1.0.0. I am documenting the build route more clearly now, with the sample results behind it.

## Where the build belongs

I keep the build in `compose.yaml`:

```yaml
services:
  api:
    build:
      context: ./app
      dockerfile: Dockerfile.test
    ports:
      - "127.0.0.1::8080"
```

Here, `Dockerfile.test` is relative to the build context. The application must listen on port 8080 inside its container. I then select Compose in the test suite:

```yaml
environment:
  source:
    driver: compose
    config: ./environment.compose.yaml
```

The separate configuration names the Compose files, binds logical targets such as `api` or `ordersdb`, and declares readiness. My assertions use those names. The [Compose guide](../../compose-environments.md) has the configuration and limits; these two fragments are only the build and suite selection.

For a smaller inline suite, I can also run `docker build` myself and put the resulting image name under `environment.services`. I choose Compose when I already have the application and dependencies described there.

## What I checked

I ran all six samples against the pinned source engine: 31 assertions passed. The three migration suites added 11 passing assertions. I also installed the official Core 1.2.1 package from the GitHub Release and ran the five stock-CLI samples: another 21 assertions passed. Every runner exited 0, with no failed, environment-error or inconclusive result.

The Compose orders sample checked the HTTP request, PostgreSQL state, Kafka event and webhook callback. The ledger sample used its custom source runner; I did not count it as an installed-CLI check. Kafka mutual TLS passed in both modes, including refusing a connection without a client certificate.

## Before using it

Vouchfx owns an isolated Compose project for each run and cleans up its resources. It supports HTTP and TCP service bindings and PostgreSQL dependencies. I use a local Docker daemon and check the guide's ownership restrictions before bringing an existing Compose file. Shared infrastructure and remote TCP/SSH daemons are unsupported. I keep the database, message and callback assertions to check the transaction after the containers become ready.

Core 1.2.1 also fixes GitLab template exit handling and patches an MSAL dependency in the VS Code packaging toolchain. MCP 1.1.0 adds language-v2 access and external-provider pre-flight support, while keeping its exact Core 1.2.0 pin.

I would start with the [orders Compose sample](https://samples.vouchfx.io/samples/orders-dotnet-compose/README.html), replace the application build, then adjust the assertions to the transaction I need to prove.
