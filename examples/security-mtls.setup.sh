#!/usr/bin/env bash
# =============================================================================
#  security-mtls.setup.sh — generate the throwaway certificates that
#  examples/security-mtls.e2e.yaml declares.
#
#  ###########################################################################
#  #  THIS IS A TEST CERTIFICATE AUTHORITY.  IT IS NOT SECURE.               #
#  #                                                                         #
#  #  Every private key it writes is unencrypted, world-generatable and      #
#  #  reproducible by anyone who runs this file.  The authority signs        #
#  #  anything asked of it and is trusted by nothing.  Do not install it,    #
#  #  do not copy it into another project, and never present anything it     #
#  #  issues to a system you did not create for the purpose.                 #
#  ###########################################################################
#
#  WHY A SCRIPT AND NOT A STEP.  vouchfx checks every path under a `security:`
#  block — that it stays inside the suite directory, and that the file is
#  actually there — BEFORE it starts a single container.  A `script.csharp`
#  step runs long after that gate, so no step can create this material in time.
#  A real adopter has the same constraint and solves it the same way: the
#  certificates come from the PKI (or from a pipeline step) before the suite is
#  handed to vouchfx.  This script stands in for that.
#
#  WHY openssl HERE AND .NET IN THE .ps1 SIBLING.  Each script uses the tool
#  its own platform guarantees.  openssl is present on essentially every Linux
#  and macOS machine and on every GitHub-hosted runner; it is NOT reliably
#  present on Windows.  PowerShell 7 is the reverse.  Rather than making either
#  reader install something, the pair produces byte-compatible output from
#  whichever tool is already there.  Both write exactly the same seven files into
#  the certificates directory, with the same permissions, and neither writes
#  anything else there.  This script keeps the CA's own key and the CSRs in a
#  temporary directory OUTSIDE the repository and deletes it on exit; the .ps1
#  never puts the CA key on disk at all.
#
#  Those are two separate promises and only one of them is absolute.  Location is
#  unconditional.  Deletion is an EXIT trap, so it covers every ending the shell
#  gets to observe — measured: normal exit, `set -e` error, SIGTERM and SIGINT all
#  leave nothing behind — but a SIGKILL bypasses every trap there is, and leaves
#  the temporary directory holding the CA key.  Being out of the tree is what
#  bounds that case: it cannot be committed, only reaped by the host.
#
#  PREREQUISITE: openssl on PATH.  Present by default on essentially every Linux
#  and macOS install and on every GitHub-hosted runner; if it is missing this
#  script says so and names the package.
#
#  USAGE  (invoked through `bash` so no executable bit is required — this
#          repository is developed on Windows, where git cannot record one)
#      bash examples/security-mtls.setup.sh           # generate if needed
#      bash examples/security-mtls.setup.sh --force   # always regenerate
#
#  Idempotent: re-running is a no-op while the existing material is still
#  valid, and regenerates automatically once it is within a day of expiry.
# =============================================================================
set -euo pipefail

readonly CA_SUBJECT="Vouchfx Example mTLS CA"
readonly CLIENT_SUBJECT="vouchfx-example-client"
readonly SERVER_SUBJECT="localhost"
readonly DAYS=30

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly certs_dir="${script_dir}/security-mtls/certs"

# Staging sits BESIDE the destination, not in /tmp, so publishing is a rename within one
# filesystem rather than a cross-device copy. See the publish step for why that matters.
readonly staging_dir="${certs_dir}.new"

force=0
if [ "${1:-}" = "--force" ]; then
  force=1
elif [ "$#" -gt 0 ]; then
  printf 'usage: %s [--force]\n' "$0" >&2
  exit 2
fi

# The seven files the suite declares. Named once, checked once, listed once.
readonly OUTPUTS=(
  ca.pem
  client.pem client-key.pem
  server.pem server-key.pem
  kafka.keystore.pem kafka.truststore.pem
)

# ── Is the existing material still usable? ───────────────────────────────────
# Three questions, not one, because "the files are there" is the weakest of them.
#
#   1. every file present and non-empty;
#   2. both leaves actually CHAIN to the ca.pem sitting beside them — which catches a set
#      assembled from two different authorities (an interrupted regeneration over an
#      existing set used to be able to produce exactly that) and a hand-edited file;
#   3. the anchor is good for another day, so a set that is valid now but expires mid-run
#      is replaced before it is used rather than after it fails.
#
# (2) subsumes an expiry check on the leaves — `openssl verify` rejects an expired
# certificate — so only the renewal MARGIN in (3) is left to ask separately.
material_is_current() {
  local f
  for f in "${OUTPUTS[@]}"; do
    [ -s "${certs_dir}/${f}" ] || return 1
  done

  openssl verify -CAfile "${certs_dir}/ca.pem" \
    "${certs_dir}/server.pem" "${certs_dir}/client.pem" >/dev/null 2>&1 || return 1

  openssl x509 -in "${certs_dir}/ca.pem" -noout -checkend 86400 >/dev/null 2>&1
}

if [ "$force" -eq 0 ] && material_is_current; then
  printf 'security-mtls: certificates in %s are current — nothing to do.\n' "$certs_dir"
  printf 'security-mtls: pass --force to regenerate them anyway.\n'
  exit 0
fi

command -v openssl >/dev/null 2>&1 || {
  printf 'error: openssl not found on PATH, and this script needs it.\n' >&2
  printf '       Install it with your package manager — for example:\n' >&2
  printf '         Debian/Ubuntu   sudo apt-get install openssl\n' >&2
  printf '         Fedora/RHEL     sudo dnf install openssl\n' >&2
  printf '         macOS           brew install openssl\n' >&2
  printf '       If you happen to have PowerShell 7, the sibling script\n' >&2
  printf '       examples/security-mtls.setup.ps1 needs no openssl at all — it uses\n' >&2
  printf '       .NET, which pwsh already carries. That is the normal route on Windows.\n' >&2
  exit 1
}

mkdir -p "$(dirname -- "$certs_dir")"

# Everything below is written into a STAGING DIRECTORY and published by swapping the
# directory itself — not by moving seven files into the live one.
#
# The difference is the whole point rather than a refinement. Publishing file by file over
# an EXISTING set has an interruption window in which the directory holds a mixture of two
# certificate authorities: every file present, every file individually well-formed, and the
# leaves no longer chaining to the anchor beside them. Nothing downstream notices — the
# currency guard used to say "nothing to do", and the compile gate only checks that the
# files exist — so the reader's next run is the first thing to fail, at the handshake, with
# a message about TLS rather than about a half-written fixture.
#
# With a directory swap the only interruption states are "old set intact" and "no set at
# all", and the second regenerates on the next run. The guard's chain check above closes
# the same hole from the other side, for any set this script did not write.
work="$staging_dir"

# THE CA'S OWN PRIVATE KEY AND THE CSRs NEVER TOUCH THE REPOSITORY. They go to a
# temporary directory outside the working tree, and only the seven published files are
# written under examples/.
#
# The reason is that a cleanup trap is not a guarantee. `trap … EXIT` does not run on
# SIGKILL, and this repository's own examples compile gate kills a timed-out setup script
# with exactly that — so "the publish step deletes it" was a promise the script could not
# keep. With the key generated out of tree there is no window at all: a killed run can
# leave an incomplete certs.new behind, but never a CA private key inside the repository.
# (The .gitignore glob covers certs.new as a second line of defence, not the first.)
secrets_work="$(mktemp -d)"
trap 'rm -rf -- "$secrets_work" "$work"' EXIT
rm -rf -- "$work"
mkdir -p "$work"

printf 'security-mtls: generating a TEST certificate authority in %s\n' "$certs_dir"

# ── 1. The private CA ────────────────────────────────────────────────────────
# One authority signs both the API's certificate and the broker's, because that
# is what an internal PKI looks like: a single anchor the suite declares once as
# `caCert` and both secured targets are validated against.
openssl req -x509 -newkey rsa:2048 -nodes \
  -keyout "${secrets_work}/ca-key.pem" -out "${work}/ca.pem" -days "$DAYS" \
  -subj "/CN=${CA_SUBJECT}" \
  -addext "basicConstraints=critical,CA:TRUE" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -addext "subjectKeyIdentifier=hash" \
  >/dev/null 2>&1

# ── 2. The server identity ───────────────────────────────────────────────────
# CN and SANs are `localhost`/127.0.0.1 because that is the address the engine
# reaches an engine-started container on, and the probe does not relax hostname
# verification. A certificate for any other name fails the handshake.
openssl req -newkey rsa:2048 -nodes \
  -keyout "${work}/server-key.pem" -out "${secrets_work}/server.csr" \
  -subj "/CN=${SERVER_SUBJECT}" \
  >/dev/null 2>&1

openssl x509 -req -in "${secrets_work}/server.csr" \
  -CA "${work}/ca.pem" -CAkey "${secrets_work}/ca-key.pem" -CAcreateserial -CAserial "${secrets_work}/ca.srl" \
  -out "${work}/server.pem" -days "$DAYS" -sha256 \
  -extfile <(printf '%s\n' \
    'basicConstraints=critical,CA:FALSE' \
    'keyUsage=critical,digitalSignature,keyEncipherment' \
    'extendedKeyUsage=serverAuth,clientAuth' \
    'subjectAltName=DNS:localhost,IP:127.0.0.1' \
    'subjectKeyIdentifier=hash') \
  >/dev/null 2>&1

# ── 3. The client identity ───────────────────────────────────────────────────
# No subject alternative name: this identity is never dialled, only presented.
# The common name is what nginx reports back as $ssl_client_s_dn and what the
# broker logs as its peer principal, so it is the example's evidence of WHO was
# authenticated.
openssl req -newkey rsa:2048 -nodes \
  -keyout "${work}/client-key.pem" -out "${secrets_work}/client.csr" \
  -subj "/CN=${CLIENT_SUBJECT}" \
  >/dev/null 2>&1

openssl x509 -req -in "${secrets_work}/client.csr" \
  -CA "${work}/ca.pem" -CAkey "${secrets_work}/ca-key.pem" -CAcreateserial -CAserial "${secrets_work}/ca.srl" \
  -out "${work}/client.pem" -days "$DAYS" -sha256 \
  -extfile <(printf '%s\n' \
    'basicConstraints=critical,CA:FALSE' \
    'keyUsage=critical,digitalSignature,keyEncipherment' \
    'extendedKeyUsage=serverAuth,clientAuth' \
    'subjectKeyIdentifier=hash') \
  >/dev/null 2>&1

# ── 4. The broker's two PEM stores ───────────────────────────────────────────
# Kafka has accepted `ssl.keystore.type=PEM` since 2.7. Its key store is ONE
# file — the private key followed by the certificate — and its trust store is
# simply the anchor. Using PEM rather than a JKS is what keeps this script free
# of a JDK dependency: no keytool, no throwaway container.
cat "${work}/server-key.pem" "${work}/server.pem" > "${work}/kafka.keystore.pem"
cp "${work}/ca.pem" "${work}/kafka.truststore.pem"

# ── 5. Publish ───────────────────────────────────────────────────────────────
# Private keys are narrowed BEFORE the swap, so they are never briefly readable at the
# live path. Nothing is deleted by hand here: the CA key, the CSRs and the serial file all
# live in $secrets_work, and the EXIT trap armed above removes it on success, on a `set -e`
# error, and on SIGTERM/SIGINT alike (all measured; SIGKILL is the one exception, and no
# trap survives that).
chmod 600 \
  "${work}/client-key.pem" \
  "${work}/server-key.pem" \
  "${work}/kafka.keystore.pem"

rm -rf -- "$certs_dir"
mv -- "$work" "$certs_dir"

# NO `trap - EXIT` HERE, and the omission is the point rather than an oversight.
#
# There was one, from when the trap's only target was $work: after the `mv` that path no
# longer exists, so disarming was a harmless belt-and-braces guard against some future
# edit deleting the freshly published directory. It stopped being harmless the moment
# $secrets_work joined the same trap — the trap became the ONLY thing that removes the CA's
# private key, and disarming it on the success path meant the normal, everyday run was the
# one that leaked. Measured before the fix: one `--force` run left a /tmp/tmp.* directory
# holding ca-key.pem, for a signing key both fixture services trust.
#
# Leaving the trap armed is safe, and that was measured too rather than assumed: $work has
# been renamed away, `rm -rf` over a path that no longer exists returns 0, the published
# directory is untouched, and the script still exits 0 under `set -euo pipefail`.

cat <<EOF
security-mtls: wrote ${#OUTPUTS[@]} files to ${certs_dir}
security-mtls:   ca.pem              the private CA both services chain to
security-mtls:   client.pem/-key     the identity the suite presents
security-mtls:   server.pem/-key     nginx's own certificate
security-mtls:   kafka.keystore.pem  the broker's key store (key + certificate)
security-mtls:   kafka.truststore.pem the broker's trust store (= ca.pem)
security-mtls: valid for ${DAYS} days. THIS IS TEST MATERIAL — the keys are
security-mtls: unencrypted and the authority is trusted by nothing. Never reuse it.
EOF
