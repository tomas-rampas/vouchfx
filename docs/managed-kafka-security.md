# Managed Kafka TLS and mutual TLS

The inline `kafka` dependency can provision the default pinned Confluent broker with
file-backed PEM TLS or mutual TLS. The engine chooses the listener mechanics and uses
Aspire's allocated host address for broker advertisement, Kafka steps and security
confirmation. You supply the certificates, private keys and trust anchors.

This recipe has been exercised from a source build on a Windows host with Linux Docker
containers and the default Confluent 8.2.0 image. It is not a released-candidate or
cross-platform acceptance claim. SASL, secured external-driver environments and other
broker images are outside this recipe.

Caller-token cancellation during broker acquisition in a normal, non-watch run has been
tested with cleanup completed before return. A completed watch loop keeps its graceful
requested-stop result; exceptions during a watch iteration retain their existing handling.
Actual Ctrl-C, SIGTERM and stdin-EOF termination remain separate acceptance
gates: their unchanged 30-second outer force-exit budget can interrupt the cancellation
route's 30-second stop allowance and subsequent disposal.

## Prepare the material

Keep the following files inside the directory containing the suite:

- `ca.pem`: the CA certificate that signs the broker certificate.
- `server.pem`: the broker certificate chain followed by its unencrypted PKCS#8 PEM
  private key. Its certificate must cover the allocated address's hostname; the local
  recipe uses `localhost` and `127.0.0.1` subject alternative names.
- For mutual TLS, `client.pem` and `client-key.pem`: a client certificate and matching
  PEM key trusted by the broker CA.

Generate these before running vouchfx. The engine checks file metadata during authoring
and copies declared server artefacts during acquisition; it does not generate certificates
or read private key contents during compilation. An unreadable file or invalid broker
material can still prevent startup after validation succeeds.

## Complete mutual TLS suite

```yaml
metadata: { name: managed-kafka-mtls }
environment:
  dependencies:
    broker:
      type: kafka
      security:
        profile: mtls
        endpoint: tcp
        caCert: ./ca.pem
        clientCert: ./client.pem
        clientKey: ./client-key.pem
        serverArtifacts:
          - { source: ./server.pem, target: /etc/kafka/secrets/server.pem }
          - { source: ./ca.pem, target: /etc/kafka/secrets/ca.pem }
      env:
        KAFKA_SSL_KEYSTORE_TYPE: PEM
        KAFKA_SSL_KEYSTORE_LOCATION: /etc/kafka/secrets/server.pem
        KAFKA_SSL_TRUSTSTORE_TYPE: PEM
        KAFKA_SSL_TRUSTSTORE_LOCATION: /etc/kafka/secrets/ca.pem
steps:
  - id: publish
    type: mq-publish.kafka
    target: broker
    topic: owned-orders
    payload: '{"id":"owned-order"}'
    timeout: 30s
  - id: consume
    type: mq-expect.kafka
    target: broker
    topic: owned-orders
    match: { payloadContains: owned-order }
    verifyMode: RETRY
    timeout: 30s
```

Save this as `broker.e2e.yaml`, then run:

```bash
vouchfx validate ./broker.e2e.yaml
vouchfx run ./broker.e2e.yaml --fail-on-env-error --fail-on-inconclusive
```

For server-authenticated TLS, change `profile` to `tls` and remove `clientCert` and
`clientKey`. `clientKeyPassword`, when used with an encrypted client key, belongs only
to that client identity; it never configures the broker's key password. Encrypted broker
keys and JKS/PKCS#12 broker stores are not covered by this tested PEM recipe.

## Select the listener without fixing a host port

`endpoint: tcp` secures the primary container listener; `endpoint: internal` secures the
other declared listener. A decimal port such as `"29092"` selects the primary listener
at that **container** port. Aspire still allocates the host port. The engine relocates
colliding auxiliary listener roles consistently; do not author advertised host ports.

The other endpoint remains plaintext for Schema Registry and container services using
`${conn:broker}`. It remains published by the current topology. The security declaration
and confirmation apply to the selected listener, not every broker listener.

Listener, advertisement, protocol-map, controller, role, REST-routing and client-auth
settings are engine-owned on a secured managed broker. Conflicting overrides are refused
before acquisition, including differently cased or dotted names that the default image
converts to the same broker property. PEM type and declared material-location checks apply
to both global `ssl.*` properties and the selected listener's
`listener.name.vouchfxsecure.ssl.*` properties, including their aliases. Kafka gives the
selected listener's material precedence over global fallback, so neither scope can bypass
preflight. Conflicting aliases of one location property are refused; global and selected
listener locations may differ when each names a declared server artefact. The uppercase
image prefix remains case-sensitive, and escaped underscores name different properties.
Other environment settings remain author-owned. Use an image-form
service for a custom broker configuration; a custom image is not covered by this default
managed recipe.

## Readiness and asynchronous consumption

Only the secured broker's default plaintext producer health check is replaced, with a
bounded TCP backend check. A socket check does not confirm TLS: before steps run, a
separate probe validates the certificate chain and hostname, performs a Kafka protocol
round trip and, for mutual TLS, checks rejection without a client identity.

`mq-expect.kafka` manually assigns every discovered topic partition at its earliest
available offset and reads retained records. A fresh client `GroupId` is configured
per attempt, but the step does not subscribe to or join a broker consumer group,
and it does not commit offsets. Broker group-rebalance delay therefore does not
govern this step. Each attempt drains for roughly one second; `RETRY` accommodates
messages arriving later. Increasing the outer timeout does not lengthen an
individual drain.

A statically invalid selected endpoint is an authoring refusal. A typed failure of the
declared secured resource's own readiness, discovery or server-material access that
prevents confirmation produces a non-zero default exit. Unrelated or unattributed
infrastructure failures retain the opt-in policy described in
[CI integration](ci-integration.md).

Custom Kafka provider authors can use the current source's public
`KafkaSecurityV2Helper.Source` and emitted `KafkaSecurity_V2_Helpers.ConfigureClient` call.
The [SDK opt-in guide](https://github.com/tomas-rampas/vouchfx/blob/main/src/Sdk/Vouchfx.Sdk/README.md#versioned-kafka-security-helper)
explains the Confluent compile reference and frozen legacy generation's diagnostic limit.
Existing providers need no rebuild merely to coexist with it.

The suite aggregate still ranks `EnvironmentError` above `Fail`. For example, a completed
assertion failure beside the declared broker's own unconfirmed readiness failure aggregates
to `EnvironmentError` and exits 3. A suite whose aggregate remains `Fail` exits 1.
