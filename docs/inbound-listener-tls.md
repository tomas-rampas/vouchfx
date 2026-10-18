# TLS for host-owned listeners

**Unreleased.** A `webhook-listen.http` listener or `trace-expect.otlp` receiver can use TLS or mutual TLS. Existing suites without a top-level `listeners:` entry continue to expose plaintext HTTP endpoints.

```yaml
listeners:
  callbacks:
    kind: webhook-listener
    security:
      profile: mtls
      serverCert: certs/listener-chain.pem
      serverKey: certs/listener-key.pem
      clientCaCert: certs/client-ca.pem
  traces:
    kind: otlp-receiver
    security:
      profile: tls
      serverCert: certs/listener-chain.pem
      serverKey: certs/listener-key.pem
steps:
  - id: callback
    type: webhook-listen.http
    listener: callbacks
    match:
      path: /events
      peerCertificateSha256: 4c6b15a5d32f4cdb772348bda08428e16ec7f6ed9f7e92b32dc3fa657a31a56e
  - id: trace
    type: trace-expect.otlp
    receiver: traces
    match:
      traceId: 0123456789abcdef0123456789abcdef
```

Each map key names a host resource contributed by a step. Its `kind` must match that step: `webhook-listener` for `webhook-listen.http`, `otlp-receiver` for `trace-expect.otlp`. Repeated steps naming one resource share its endpoint and capture buffer. An entry alone does not start a listener. The map can coexist with either inline `environment` or `environment.source`.

`profile: tls` needs `serverCert` and `serverKey` and does not accept `clientCaCert`. `profile: mtls` also requires `clientCaCert`; the listener rejects requests without a client certificate chaining to that CA during the TLS handshake. An invalid request creates no webhook or span capture. Client certificate revocation is not checked. There is no HTTPS-to-HTTP fallback.

The certificate and key are PEM files. The certificate file contains a leaf followed by optional intermediates; the matching key may be plain or encrypted PKCS#8. For an encrypted key, `serverKeyPassword` must be one whole `${secret:source/path}` reference. Do not set it for an unencrypted key. The server leaf must be currently valid and advertise the IP SAN `127.0.0.1` and DNS SAN `host.docker.internal`, so both the local and container-facing URLs can be verified. Certificate paths are relative to the suite file's directory and checked using the existing lexical trusted-workspace path policy. `vouchfx validate` checks their location, availability and public certificate names; `run` loads the private key at first use, after topology startup and before publishing a listener URL or executing a step.

The staged `svc::<name>`, `<name>` and `<name>_container` values use `https://` for configured listeners. The container form uses `host.docker.internal`; the webhook URL retains its random path token, while the OTLP value remains a base URL for the SUT's SDK to append `/v1/traces`. The SUT must trust the server certificate and, for mTLS, present a suitable client identity. Container routing to the host is required separately.

On mTLS, an accepted capture records the validated client's leaf DER SHA-256 fingerprint as lowercase 64-character hex. Optional `match.peerCertificateSha256` compares it case-insensitively on either Core step; this criterion requires a named mTLS listener. It is absent on plaintext and server-only TLS captures. A timeout means no matching capture arrived; it does not distinguish a silent SUT, a network/trust failure, or a rejected handshake.

Inbound declarations join the existing security-assurance census. A declared listener whose identity was never started and confirmed makes the default run exit nonzero, including early and mixed-suite refusals. Outbound target security and its existing confirmation behavior remain unchanged.
