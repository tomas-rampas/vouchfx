# Extra broker configuration for the `orders-broker` service of
# examples/security-mtls.e2e.yaml.
#
# This is the SERVER half of mutual TLS for Kafka. As with the nginx file beside
# it, vouchfx does not write this: you configure the broker, vouchfx configures
# the client and then proves the connection was what you declared.
#
# The Confluent image sources /etc/confluent/docker/bash-config before it
# renders the broker properties, so exporting KAFKA_* here is equivalent to
# setting them in `env:` — with one difference that matters: this file can make
# the secured listener CONDITIONAL on the key store having actually arrived.
# If `serverArtifacts` failed to deliver it, the broker comes up with no SECURE
# listener at all and the suite fails at the handshake, loudly. It never comes
# up half-secured.
#
# Delivered to /etc/confluent/docker/bash-config (see `serverArtifacts`).

# NO `set -o nounset -o errexit` HERE. This file is SOURCED by the Confluent image's own
# entrypoint, so a shell option set at the top applies to everything that entrypoint does
# afterwards, not to these twenty-odd lines — a scope nobody reviewing this file is
# thinking about, and one that turns an unset variable somewhere in Confluent's scripts
# into an abrupt exit. The only thing those options actually protected here was the
# expansion below, so that expansion guards itself, locally:
# (No apostrophe in that message, deliberately: bash parses the word inside ${var:?word}
# for quoting even within double quotes, so a stray ' makes the whole sourced file a
# syntax error — measured, and it silently costs you the SECURE listener rather than
# announcing itself.)
: "${VOUCHFX_SECURE_ADVERTISED:?must be set in the suite env: block to the pinned host address}"

if [ -f /etc/kafka/secrets/kafka.keystore.pem ]; then
  # THE LISTENER NAME `SECURE` IS LOAD-BEARING. DO NOT RENAME IT TO `SSL`.
  #
  # The Confluent image's own `configure` script greps KAFKA_ADVERTISED_LISTENERS for the
  # literal string `SSL://`, and when it matches it insists on the JKS-shaped configuration
  # this fixture deliberately does not use: KAFKA_SSL_KEYSTORE_FILENAME plus
  # KAFKA_SSL_KEY_CREDENTIALS and KAFKA_SSL_KEYSTORE_CREDENTIALS — password files that a PEM
  # store holding an unencrypted key has no equivalent of.
  #
  # Measured: rename SECURE to SSL, change nothing else, and the container exits 1 with
  # `KAFKA_SSL_KEYSTORE_FILENAME is required.` before Kafka starts; through the engine that
  # surfaces as a HEALTH GATE failure and exit 3. Neither diagnostic mentions the rename, so
  # this is a bad one to discover by experiment. Any name that is not `SSL` works — the
  # protocol is chosen by KAFKA_LISTENER_SECURITY_PROTOCOL_MAP below, not by the name.
  #
  # In fairness about provenance: `SECURE` was inherited from this repository's own mutual-TLS
  # drill fixture, not chosen because someone knew about the grep. Peer review found the trap
  # afterwards and measured it; the comment exists so the next reader gets it for free.
  #
  # Three listeners, and note WHICH INTERFACE each binds to — the difference is a real
  # boundary, not bookkeeping.
  #
  #   SECURE    0.0.0.0  — must be reachable from outside the container, because the host
  #                        port published in `ports:` forwards to it. This is the only
  #                        listener anything off-container can reach, and it demands a
  #                        client certificate.
  #   PLAINTEXT 127.0.0.1 — inter-broker traffic for this single node, which never leaves
  #   CONTROLLER 127.0.0.1  the container. Bound to loopback DELIBERATELY.
  #
  # Binding those two to 0.0.0.0 — the obvious thing to write, and what this file did
  # first — does NOT merely "expose them on the host": Docker's published-port mapping is
  # not the boundary that matters. Every other container in the suite's own topology network
  # can reach 0.0.0.0-bound ports directly, with no credentials at all. Measured on this
  # fixture before the change: a peer container connected to 9092, created a topic, and both
  # produced and consumed on this suite's own `orders` topic — completely bypassing the
  # mutual TLS that the whole example is about. Loopback closes that: a peer container now
  # gets connection refused, while the broker's own internal traffic is unaffected.
  export KAFKA_LISTENERS="PLAINTEXT://127.0.0.1:9092,SECURE://0.0.0.0:9093,CONTROLLER://127.0.0.1:9094"

  # VOUCHFX_SECURE_ADVERTISED is set in the suite's `env:` block to the PINNED
  # host port. See the suite's comment on `ports:` for why that pin is
  # load-bearing rather than tidy.
  export KAFKA_ADVERTISED_LISTENERS="PLAINTEXT://localhost:9092,SECURE://${VOUCHFX_SECURE_ADVERTISED}"
  export KAFKA_LISTENER_SECURITY_PROTOCOL_MAP="CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,SECURE:SSL"

  # PEM rather than JKS — Kafka has read PEM stores since 2.7, which is what
  # lets the setup script generate this material with no JDK on the host.
  export KAFKA_SSL_KEYSTORE_TYPE="PEM"
  export KAFKA_SSL_KEYSTORE_LOCATION="/etc/kafka/secrets/kafka.keystore.pem"
  export KAFKA_SSL_TRUSTSTORE_TYPE="PEM"
  export KAFKA_SSL_TRUSTSTORE_LOCATION="/etc/kafka/secrets/kafka.truststore.pem"

  # `required`, not `requested`. This is what makes the broker DEMAND an
  # identity, and it is what lets vouchfx report the stronger of its two
  # confirmation levels: the engine opens a second, anonymous connection and
  # shows that the broker refuses it.
  export KAFKA_SSL_CLIENT_AUTH="required"

  # ── AUTHENTICATION IS NOT AUTHORISATION, and this fixture has only the first ──────
  #
  # No `authorizer.class.name` is configured, so Kafka enforces no ACLs. Every identity
  # this CA issues is therefore an unrestricted super-user: it can read and write every
  # topic, create and delete topics, and reconfigure the cluster. Demonstrated on this
  # fixture — the suite's own client certificate created a topic, which nothing in the
  # suite needs it to do.
  #
  # That is acceptable HERE, where the CA exists for the length of one test run and issues
  # exactly two leaves. It is not acceptable anywhere else. If you copy this file towards a
  # real broker, mutual TLS gets you a verified NAME and nothing more; deciding what that
  # name may do is a separate mechanism you still have to add
  # (`authorizer.class.name=org.apache.kafka.metadata.authorizer.StandardAuthorizer`,
  # `super.users`, and per-principal ACLs).
  #
  # The example deliberately does not configure ACLs: doing so would double its size and
  # teach Kafka administration rather than what vouchfx does. Note also what vouchfx can
  # and cannot tell you here — its `AuthenticatedRoundTrip` confirmation proves the broker
  # required an identity, and says nothing about that identity's permissions. A rejected
  # authorisation surfaces later, as an ordinary step-level failure.
fi
