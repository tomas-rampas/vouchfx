---
date: 2026-08-25
authors:
  - tomas-rampas
categories:
  - Releases
tags:
  - Aspire
  - Kafka
  - mTLS
  - Configuration
---

# You can now configure a managed dependency

v1.0.0-rc.5 is released. `environment.dependencies` accepts an `env:` map, so a managed Postgres, Kafka or SQL Server can be configured the way a service already could.

<!-- more -->

```yaml
environment:
  dependencies:
    billing-db:
      type: sqlserver
      env:
        MSSQL_COLLATION: Latin1_General_CS_AS
```

That is the whole feature. The collation is measured, not illustrative: unset, that image reports `SQL_Latin1_General_CP1_CI_AS`; with the line above, `SERVERPROPERTY('Collation')` returns the declared value.

### Side by side with a service

The two blocks look identical and differ in what they accept:

```yaml
environment:
  services:
    api:                                    # your system under test
      image: acme/orders-api:latest
      env:
        DB: "${conn:billing-db}"            # ✅ services consume connections
        REGION: "${env:AWS_REGION}"         # ✅ from the engine's own environment
        TIMEOUT: 30                         # ✅ bare scalar, kept as "30"

  dependencies:
    billing-db:                             # provisioned by the engine
      type: sqlserver
      env:
        MSSQL_COLLATION: Latin1_General_CS_AS
        REGION: "${env:AWS_REGION}"         # ✅ same contract as a service
        UPSTREAM: "${conn:other-db}"        # ❌ refused — a dependency is a source
        ADMIN_PW: "${secret:vault/db/pw}"   # ❌ refused — readable via docker inspect
```

A dependency is a connection *source*, not a consumer, so barring `${conn:}` there removes inter-dependency cycles outright. `${secret:}` is barred because a container's environment is readable by anyone who can run `docker inspect`.

### So how do I give a dependency a password?

You don't, and that is the design rather than a gap. Aspire generates the credentials for a managed dependency, and `${conn:billing-db}` hands the resolved connection string — password included — to whatever consumes it. Your suite never needs the literal, which is why refusing `${secret:}` there costs you nothing.

There is one trap worth knowing. On `sqlserver` you *can* write `MSSQL_SA_PASSWORD` in a dependency's `env:`, because the engine does not reserve it — Aspire sets it, not the engine, and the refusal only covers what the engine writes. Your value wins on the container while `${conn:}` keeps advertising Aspire's generated one, so every consumer gets a password that no longer works.

If you genuinely need a specific credential — matching a fixture, or a backend someone else configured — the dependency form is the wrong shape. Declare it as a service and own the wiring:

```yaml
environment:
  services:
    billing-db:
      image: mcr.microsoft.com/mssql/server:2022-latest
      ports: ["11433:1433"]
      env:
        ACCEPT_EULA: "Y"
        MSSQL_SA_PASSWORD: "${env:DB_SA_PASSWORD}"
```

Still not `${secret:}`: that is refused in every `env:` map, service or dependency alike, for the same reason. `${secret:}` is for material the engine resolves in its own process and never writes into a container — at environment level, `security.clientKeyPassword` is the one such field.

### The refusal, per type

```yaml
environment:
  dependencies:
    search:
      type: elasticsearch
      env:
        ES_JAVA_OPTS: "-Xms2g -Xmx2g"       # ❌ refused: engine sets this on elasticsearch
        xpack.security.enabled: "true"      # ❌ refused
        VOUCHFX_PROBE: "applied"            # ✅ not an engine-set name

    orders-db:
      type: postgres
      env:
        ES_JAVA_OPTS: "-Xms2g -Xmx2g"       # ✅ applied — nothing reserves it on postgres
```

The check is per type, not a global denylist. The whole diagnostic on the refused line, before any container starts — the engine emits it as a single line, wrapped here to fit:

```
Dependency 'search' (type 'elasticsearch') declares env entry 'ES_JAVA_OPTS', which the
engine sets itself for this dependency type. That entry is REFUSED: the engine relies on
its engine-set variables to bring this dependency up in the shape every scenario shares —
and on 'minio' they are the credentials ${conn:<dependency>} advertises to every other
scenario consuming it — so honouring an override would break other scenarios rather than
only this one. Remove the entry, or declare the backend as a service with 'image:' if you
need full control of its environment.
```

That last clause is the escape hatch. If you genuinely need to own every variable on a backend, declare it under `services:` with an `image:` — you then get full control, and full responsibility for its wiring.

## Names the engine sets are refused

A managed dependency is not your container. The engine sets values on it that other scenarios depend on: `minio`'s root credentials, for instance, are spliced into the connection string `${conn:<dependency>}` hands to every scenario consuming that dependency. Overriding one does not break your scenario — it breaks everybody else's, silently.

Aspire's `WithEnvironment` is last-write-wins, so ordering cannot protect those values. Instead, an `env:` key naming a variable the engine sets for that dependency's `type:` is **refused** before any container starts, naming the variable, the dependency and the type. Nine names across three types (`elasticsearch`, `minio`, `azureservicebus`), matched per type — a name reserved for `elasticsearch` is fine on `postgres`.

**The refusal covers what the engine sets, not what Aspire sets.** `MSSQL_SA_PASSWORD` is reserved on `azureservicebus`, where the engine writes it, and unreserved on `sqlserver`, where Aspire does — so on `sqlserver` your value silently wins, connection string included. That gap is documented, not closed.

## Two things to know before you use it

**Key order matters.** Two scenarios sharing an `environment` block whose dependency `env:` keys are written in a different order are treated as divergent and abort the suite. In watch mode, a save that merely reorders two keys rebuilds the topology.

**A refusal exits 0 by default.** The check runs on the `run` path, so `vouchfx validate` will not report it, and the verdict is Inconclusive — a flagless run exits 0 having started no container and run no step. Use `--fail-on-inconclusive` in CI. (A suite declaring `security:` is the exception; it exits non-zero on its own.)

## A worked mTLS sample

`vouchfx-samples` gains [`kafka-mtls`](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/kafka-mtls): a produce/consume round trip over a mutually-authenticated Kafka broker, with plain PEM material — no JKS, no JDK.

It writes no negative control on purpose. Under `profile: mtls` the engine's own pre-run probe already does both halves before step 1: a completed authenticated round trip, and a second connection presenting no client certificate that the broker must refuse. A hand-written "expect a refusal" step would only re-prove that.

The broker is declared as a **service**, not a `kafka` dependency, and that is not a stylistic choice. Securing a dependency-form broker means adding a TLS listener, which means writing `KAFKA_ADVERTISED_LISTENERS`, which must name the host port — and a dependency has no way to know the port Aspire allocates it. CI made the point concretely: Aspire picked 39725, the suite could only write a literal, and the broker advertised a port nothing listened on.

So `security:` is accepted on a `kafka` dependency while being impossible to satisfy. That is [#443](https://github.com/tomas-rampas/vouchfx/issues/443), and it is the next thing to fix — most likely by having the engine write the listener wiring itself from the `security:` block, so you never touch it.

One honest consequence: **the sample demonstrates the mTLS path, not the dependency-`env:` path.** A sample for the headline feature is waiting on #443.

## Links

- [v1.0.0-rc.5 release notes](https://github.com/tomas-rampas/vouchfx/releases/tag/v1.0.0-rc.5)
- [`kafka-mtls` sample](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/kafka-mtls)
- [#443 — a secured kafka dependency cannot advertise a reachable address](https://github.com/tomas-rampas/vouchfx/issues/443)
