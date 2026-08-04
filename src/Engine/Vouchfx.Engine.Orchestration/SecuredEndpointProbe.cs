// Vouchfx.Engine.Orchestration — SecuredEndpointProbe (authenticated-infrastructure-mtls,
// slice E — REQ-005, EDGE-004, EDGE-005, EDGE-010).
//
// Runs after the topology is health-gated and BEFORE the first step executes, for every
// dependency and service that declares `security`. A failure raises OrchestrationException with
// Kind = SecurityConfirmation, which ScenarioRunner/HeadlessTopology already map to suite-level
// Verdict.EnvironmentError — no step runs and no Pass is reported — and which REQ-018 keys the
// unconditional non-zero exit on.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHAT A COMPLETED HANDSHAKE PROVES, AND WHAT IT DOES NOT. This is the decision the whole file
// turns on, so it is recorded with the measurements that forced it.
//
// REQ-005 as originally written says the probe confirms security by completing a TLS handshake
// that presents the declared client certificate. MEASURED on a mutual-TLS test bed, that is NOT
// sufficient, and believing it would rebuild the exact false assurance this feature exists to
// destroy. Against nginx with `ssl_verify_client on` and NO client certificate presented, from
// .NET 8 on this host:
//
//     handshake=COMPLETED proto=Tls13 localCert=none   READ=TIMED-OUT after 4001ms
//
// The handshake completes in full and the server rejects at the HTTP layer with 400. This is not
// an nginx quirk: in TLS 1.3 the client's Certificate and Finished messages arrive AFTER the
// server's Finished, so a server cannot fail the handshake synchronously on a missing or
// unacceptable client certificate. A completed handshake therefore proves the SERVER's identity
// and the transport, and says NOTHING about whether the client's certificate was accepted.
//
// So this probe does not stop there. It performs an APPLICATION-LAYER ROUND TRIP wherever the
// target's protocol is known — from the declared dependency `type`, or from the suite's own
// `mq-publish.kafka`/`mq-expect.kafka` steps naming the target (see SpeaksKafka; REQ-011's
// customer-supplied broker is authored as a SERVICE, so the declaration kind alone would hand the
// strong level to the shape nobody uses) — and states the limit precisely where it is not.
// Measured, all arms, three repeats of the positive:
//
//   Kafka broker, SSL listener with ssl.client.auth=required, ApiVersions(v0) over the TLS session
//     valid client certificate   -> RESPONSE errorCode=0 apiKeys=60          in  40-160 ms
//     no client certificate      -> IOException "The decryption operation failed"
//                                   (inner Win32Exception "…processing the certificate")  in 17 ms
//   nginx 8443 (TLS, but not Kafka), same request
//                                -> EndOfStreamException                                  in 41 ms
//   Kafka PLAINTEXT listener (9092), TLS attempted
//                                -> IOException "Received an unexpected EOF"              in 27 ms
//
// All four negatives are distinguished, in tens of milliseconds. The Kafka arm is therefore
// OPTION 1 of the two honest choices — the strong one — and it is what a Kafka-speaking target gets.
//
// REJECTED, with the measurement that rejected it: driving the same confirmation through
// Confluent.Kafka's own AdminClient.GetMetadata. Against the identical broker, with a VALID
// client certificate, it threw `KafkaException: Local: Timed out` after 10.2 s — because metadata
// resolution follows the broker's `advertised.listeners`, which named a container hostname the
// engine host cannot resolve. That is a real deployment concern, but it is not a security fault,
// and reporting it as one would fire REQ-018's unconditional non-zero exit for a reason unrelated
// to security. A direct ApiVersions exchange asks the connected broker and nothing else, needs no
// new engine dependency, and cannot be defeated by an advertised-listener mismatch.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// ONE COMPLETED ROUND TRIP IS STILL NOT ENOUGH, AND THIS IS THE SECOND MEASUREMENT THAT FORCED
// THE DESIGN. "A peer refusing the presented certificate cannot complete the round trip" is true.
// Concluding THEREFORE IT ACCEPTED requires the peer to have refused something — and a peer that
// never REQUESTED a certificate refuses nothing. Kafka's `ssl.client.auth` defaults to `none`, so
// a broker whose keystore lands correctly (EDGE-005 does not fire) but whose `KAFKA_SSL_CLIENT_AUTH`
// is simply unset comes up with a perfectly working SSL listener that ignores client certificates
// entirely. Everything would be green, and the engine would print a sentence asserting the
// opposite.
//
// So for `profile: mtls` this probe opens a SECOND connection to the same address presenting NO
// client certificate, and FAILS CLOSED if that also completes the round trip. REQ-005's own wording
// requires exactly this — the strong level is defined as "an application-layer exchange the endpoint
// WOULD REFUSE AN UNAUTHENTICATED CLIENT", which is a claim about the refusal, not about the
// acceptance, and the only way to hold it is to try being that unauthenticated client.
//
// Measured, .NET 8 on this host, loopback TLS listener + hand-framed ApiVersions, 3 repeats, all
// identical (the arm-1 message text VARIES run to run — "An established connection was aborted…"
// then "An unknown error occurred while processing the certificate" — so no message is asserted on,
// only the outcome):
//
//   client        server `ssl.client.auth` analogue          outcome
//   ───────────── ───────────────────────────────────────── ─────────────────────────────────────
//   NO cert       required   (requested + refuses null)      FAILED (IOException)          ~6 ms
//   NO cert       requested  (requested, absence tolerated)  ROUND TRIP SUCCEEDED  ← the detection
//   NO cert       none       (never requested)               ROUND TRIP SUCCEEDED  ← the detection
//   WITH cert     required                                   ROUND TRIP SUCCEEDED  ← positive control
//   WITH cert     requested                                  ROUND TRIP SUCCEEDED
//
// The positive control is what rules out the trivial explanation "everything fails": with the
// declared identity the SAME exchange against the SAME enforcing listener succeeds in ~5 ms. Cost
// is one extra connection per `mtls` Kafka-speaking target, inside the same perTargetTimeout.
//
// REJECTED, with the measurement that rejected it: `SslStream.IsMutuallyAuthenticated`. In the
// `WITH cert / requested` arm above it reports **True** and the server genuinely did receive the
// certificate — while the `NO cert / requested` arm proves the same listener would have accepted an
// anonymous client just as happily. The property answers "did I send one and did it get validated",
// never "would this peer have refused me without it", so it is True in precisely the case this
// section exists to catch. (The security gate independently refused it for a different reason: one
// unexplained contradictory reading among twelve. Two independent grounds, same conclusion.) The
// differential is protocol-level, had no outlier across 3 repeats, and answers the actual question.
//
// For a SERVICE the declaration carries no protocol, so no application-layer round trip is
// available — sending Kafka framing to an HTTP endpoint would corrupt the connection and prove
// nothing. That target gets the transport confirmation plus a bounded wait for an explicit
// TLS-layer rejection, and its report says in words that client-certificate ACCEPTANCE is
// confirmed at first step execution rather than here. Measured, both arms identical, so the wait
// genuinely cannot distinguish them and is not claimed to: nginx 8443 WITH a valid client
// certificate and WITHOUT one both read `TIMED-OUT after ~4000ms`.
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// It must NOT be expressed as an Aspire health check. A container health check cannot present a
// client certificate, so no health-gating mechanism can confirm a mutual-TLS listener — only that
// something accepted a socket. Health-gate the container, then probe the security: separate
// stages, separate mechanisms. That is why this lives in the engine at all.
using System.Buffers.Binary;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// How far the engine got in confirming one declared <c>security</c> block.
/// </summary>
public enum SecurityConfirmationLevel
{
    /// <summary>
    /// The endpoint speaks TLS, its certificate satisfied the declared trust anchor (or the
    /// platform's own trust store when no <c>caCert</c> is declared), and the declared client
    /// certificate was PRESENTED — but nothing confirms the peer ACCEPTED it, because the
    /// target's application protocol is not known from its declaration and a completed TLS 1.3
    /// handshake carries no such signal (see this file's header).
    /// </summary>
    TransportConfirmed,

    /// <summary>
    /// The endpoint completed an application-protocol round trip over the secured connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For <c>profile: mtls</c> this level additionally means the engine confirmed the peer
    /// REFUSES an anonymous client: a second connection presenting no client certificate did not
    /// complete the same round trip. Both halves are required, because a completed round trip
    /// alone proves only that the peer did not object — and a peer that never asked for a
    /// certificate objects to nothing (see this file's header).
    /// </para>
    /// <para>
    /// For <c>profile: tls</c> there is no client identity to have accepted, and none is claimed:
    /// the round trip confirms the transport and that the peer really speaks the protocol the
    /// steps will speak. The confirmation's own <c>Detail</c> says which of the two it is.
    /// </para>
    /// <para>
    /// It says nothing about AUTHORISATION — whether that identity may publish to or consume from
    /// a particular topic (EDGE-011). That is enforced per request by the broker's own authoriser,
    /// is inherently step-specific, and surfaces as an ordinary step-level environment error.
    /// </para>
    /// </remarks>
    AuthenticatedRoundTrip,
}

/// <summary>
/// What one declared <c>security</c> block asserted and what the engine actually observed
/// (REQ-005: "report declared-versus-observed rather than only a boolean").
/// </summary>
/// <param name="TargetName">The declared service or dependency name.</param>
/// <param name="TargetKind">
/// <c>"service"</c>, or the dependency's declared <c>type</c> (e.g. <c>"kafka"</c>).
/// </param>
/// <param name="DeclaredProfile">The <c>security.profile</c> as authored.</param>
/// <param name="DeclaredEndpoint">The <c>security.endpoint</c> selector as authored.</param>
/// <param name="ObservedAddress">The host-side <c>host:port</c> the engine actually reached.</param>
/// <param name="ObservedProtocol">The negotiated TLS protocol, e.g. <c>"Tls13"</c>.</param>
/// <param name="ClientIdentityResolved">
/// <see langword="true"/> when a client certificate was resolved for this target and configured on
/// the handshake.
/// <para>
/// <strong>Named for what it measures.</strong> It is set from "a client certificate was resolved",
/// which is a fact about the DECLARATION, not about the wire. Whether the platform actually put that
/// certificate on the wire depends on the peer sending a <c>CertificateRequest</c>, which this side
/// cannot observe: measured, <c>SslStream.LocalCertificate</c> and
/// <c>SslStream.IsMutuallyAuthenticated</c> both report a presented, mutually-authenticated session
/// against a listener that requested a certificate but would equally have accepted none (see this
/// file's header). The claim that the peer ACCEPTED an identity lives in
/// <see cref="SecurityConfirmationLevel.AuthenticatedRoundTrip"/>, which is measured, and nowhere
/// else.
/// </para>
/// </param>
/// <param name="Level">How far confirmation got.</param>
/// <param name="Detail">A one-line human-readable summary of what was confirmed.</param>
public sealed record SecurityConfirmation(
    string TargetName,
    string TargetKind,
    string DeclaredProfile,
    string DeclaredEndpoint,
    string ObservedAddress,
    string ObservedProtocol,
    bool ClientIdentityResolved,
    SecurityConfirmationLevel Level,
    string Detail)
{
    /// <summary>
    /// Renders this confirmation as one declared-versus-observed line for the run's own output.
    /// </summary>
    public override string ToString() =>
        $"security: {TargetKind} '{TargetName}' declared profile '{DeclaredProfile}' on endpoint "
        + $"'{DeclaredEndpoint}'; observed {ObservedProtocol} at {ObservedAddress}, client identity "
        + $"{(ClientIdentityResolved ? "resolved" : "none declared")} — {Detail}";
}

/// <summary>
/// The fail-closed secured-confirmation probe (REQ-005).
/// </summary>
internal static class SecuredEndpointProbe
{
    /// <summary>
    /// The Kafka <c>ApiVersions</c> API key. Chosen because it is the one request every broker
    /// answers before any authorisation decision is reached, at every protocol version, with no
    /// topic, group or cluster state involved.
    /// </summary>
    private const short ApiVersionsApiKey = 18;

    /// <summary>
    /// How long to wait for an explicit TLS-layer rejection on a target whose application
    /// protocol is unknown. Measured: a Kafka broker refusing a client certificate raises the
    /// fatal alert within 17 ms, so 1 s is generous by roughly sixty times while costing an
    /// unsecured-peer-free run exactly that much per secured service.
    /// </summary>
    private static readonly TimeSpan RejectionGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Confirms every declared <c>security</c> block against the running topology.
    /// </summary>
    /// <param name="environment">The suite's environment declaration.</param>
    /// <param name="discoveredServices">
    /// The resolved endpoint URLs and connection strings, exactly as
    /// <c>MappedTopology.ResolveServices</c> produced them.
    /// </param>
    /// <param name="security">
    /// The resolved client security configuration (REQ-014). The probe presents the SAME
    /// MATERIAL a step will — the same accessor, the same declared paths, the same certificate
    /// objects — which is what lets it fail before a step does.
    /// <para>
    /// <strong>The material is shared; the judge is not, and the difference matters for a Kafka
    /// target.</strong> This probe's peer verdict is .NET <c>SslStream</c>'s, applied to the
    /// host-published address the topology staged. A <c>mq-publish.kafka</c> step's peer verdict is
    /// librdkafka/OpenSSL's, applied to whatever the broker's <c>advertised.listeners</c> names,
    /// which is frequently a different address. The risk direction is safe — librdkafka enables
    /// certificate verification by default and the engine's helper overrides neither that nor the
    /// hostname check, so the step is never LESS strict than this probe — but the two are not the
    /// same judgement and this file does not claim they are.
    /// </para>
    /// </param>
    /// <param name="kafkaSpeakingTargets">
    /// The declared target names the suite's own steps address with a Kafka-protocol step
    /// (<c>mq-publish.kafka</c> / <c>mq-expect.kafka</c>). A target in this set earns the
    /// application-layer round trip even when it is declared as a SERVICE rather than as the
    /// engine-provisioned <c>kafka</c> dependency type — which is the shape REQ-011 exists for, and
    /// therefore the shape the customer's own broker takes. See <see cref="SpeaksKafka"/>.
    /// </param>
    /// <param name="perTargetTimeout">Bound on each target's connect + handshake + round trip.</param>
    /// <param name="cancellationToken">Propagated to every socket and stream operation.</param>
    /// <returns>One confirmation per declared block, in services-then-dependencies order.</returns>
    /// <exception cref="OrchestrationException">
    /// A declared block could not be confirmed. <c>Info.Kind</c> is
    /// <see cref="OrchestrationErrorKind.SecurityConfirmation"/> and <c>Info.ResourceName</c> is
    /// the declared target name.
    /// </exception>
    internal static async Task<IReadOnlyList<SecurityConfirmation>> ConfirmAsync(
        EnvironmentSpec? environment,
        IReadOnlyDictionary<string, object> discoveredServices,
        ISecurityConfigurationAccessor security,
        IReadOnlySet<string> kafkaSpeakingTargets,
        TimeSpan perTargetTimeout,
        CancellationToken cancellationToken)
    {
        var confirmations = new List<SecurityConfirmation>();

        foreach (var (name, kind, spec) in EnumerateSecuredTargets(environment))
        {
            confirmations.Add(await ConfirmOneAsync(
                    name,
                    kind,
                    spec,
                    discoveredServices,
                    security,
                    kafkaSpeakingTargets,
                    perTargetTimeout,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return confirmations;
    }

    /// <summary>
    /// Yields every declared target carrying a <c>security</c> block: services first, then
    /// dependencies — the same order <c>EnvironmentSecurityValidator</c> walks, so a suite with
    /// two faults reports the same one at both stages.
    /// </summary>
    private static IEnumerable<(string Name, string Kind, SecuritySpec Spec)> EnumerateSecuredTargets(
        EnvironmentSpec? environment)
    {
        if (environment?.Services is { } services)
        {
            foreach (var (name, spec) in services)
            {
                if (spec.Security is { } declared)
                {
                    yield return (name, "service", declared);
                }
            }
        }

        if (environment?.Dependencies is { } dependencies)
        {
            foreach (var (name, spec) in dependencies)
            {
                if (spec.Security is { } declared)
                {
                    yield return (name, spec.Type, declared);
                }
            }
        }
    }

    private static async Task<SecurityConfirmation> ConfirmOneAsync(
        string name,
        string kind,
        SecuritySpec spec,
        IReadOnlyDictionary<string, object> discoveredServices,
        ISecurityConfigurationAccessor security,
        IReadOnlySet<string> kafkaSpeakingTargets,
        TimeSpan perTargetTimeout,
        CancellationToken cancellationToken)
    {
        var declaredProfile = spec.Profile ?? string.Empty;
        var declaredEndpoint = spec.Endpoint ?? string.Empty;

        if (!TryResolveAddress(name, discoveredServices, out var host, out var port))
        {
            throw Failure(
                name,
                $"declared security (profile '{declaredProfile}', endpoint '{declaredEndpoint}') but the "
                + $"topology staged no reachable address for '{name}', so there is nothing to confirm. "
                + "A secured target must be one the engine itself starts and publishes an endpoint for.");
        }

        var observedAddress = FormattableString.Invariant($"{host}:{port}");
        var configuration = ResolveConfiguration(name, declaredProfile, declaredEndpoint, observedAddress, security);
        var certificates = configuration?.Certificates;

        // The SAME material a step will use, resolved through the SAME accessor — which is what
        // makes a probe pass evidence about the step rather than about the probe. Reading these
        // properties re-checks path containment (REQ-003) and loads the files, so a declared
        // certificate that exists but is malformed surfaces HERE, before any container work is
        // wasted, naming the declared path.
        X509Certificate2? clientCertificate = null;
        var presentsClientIdentity = string.Equals(declaredProfile, "mtls", StringComparison.Ordinal);
        if (presentsClientIdentity)
        {
            try
            {
                clientCertificate = certificates?.ClientCertificate;
            }
            catch (SecurityMaterialException ex)
            {
                throw Failure(
                    name,
                    $"declared profile 'mtls' on endpoint '{declaredEndpoint}', but its client identity "
                    + $"could not be loaded: {ex.Message}",
                    ex);
            }

            if (clientCertificate is null)
            {
                throw Failure(
                    name,
                    $"declared profile 'mtls' on endpoint '{declaredEndpoint}', but no "
                    + "'clientCert'/'clientKey' pair resolved for it. Mutual TLS with no client identity "
                    + "is an unauthenticated connection wearing the word 'mutual'.");
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(perTargetTimeout);

        var tcp = new TcpClient();
        SslStream? tls = null;
        try
        {
            try
            {
                await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // EDGE-005 reaches here when the broker came up with no SSL listener at all: the
                // port its entrypoint would have opened is simply refused, while the container
                // itself reports healthy and no ordinary infrastructure signal ever failed.
                throw Failure(
                    name,
                    $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}', but the "
                    + $"engine could not connect to {observedAddress}: {Summarise(ex)}",
                    ex);
            }

            tls = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                BuildValidationCallback(certificates));

            var options = new SslClientAuthenticationOptions
            {
                // Hostname verification is NOT relaxed here, and a declared caCert never forgives
                // it (REQ-024's rule, applied identically on this path): a CA says which issuer to
                // trust, never which host. The system under test's own certificate must therefore
                // be valid for the address the engine reaches it on — for an engine-started
                // container that is the published loopback address, which is the same constraint
                // the HTTP family already carries.
                TargetHost = host,

                // Leave protocol selection to the platform's own defaults rather than pinning a
                // version here: pinning would silently diverge from what the step's own client
                // negotiates, and the whole value of this probe is that it tests the same thing.
                EnabledSslProtocols = SslProtocols.None,
            };

            if (clientCertificate is not null)
            {
                options.ClientCertificates = new X509CertificateCollection { clientCertificate };
            }

            try
            {
                await tls.AuthenticateAsClientAsync(options, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                // EDGE-004 reaches here: a suite whose endpoint resolved to the plaintext listener
                // the infrastructure keeps open alongside the secured one. Measured against a
                // Kafka PLAINTEXT listener — IOException "Received an unexpected EOF or 0 bytes
                // from the transport stream" in 27 ms. A plaintext port cannot fake a handshake.
                throw Failure(
                    name,
                    $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}', but the TLS "
                    + $"handshake against {observedAddress} failed: {Summarise(ex)}. The endpoint may not "
                    + "be running TLS at all (a plaintext listener alongside the secured one is the "
                    + "usual cause), or its certificate may not satisfy the declared trust anchor.",
                    ex);
            }

            var observedProtocol = tls.SslProtocol.ToString();

            // ── The application-layer confirmation (option 1), where the protocol is known ─────
            if (SpeaksKafka(name, kind, kafkaSpeakingTargets))
            {
                await ConfirmKafkaRoundTripAsync(
                        name, declaredProfile, declaredEndpoint, observedAddress, tls, timeout.Token)
                    .ConfigureAwait(false);

                if (clientCertificate is not null)
                {
                    // The differential this file's header records: one completed round trip proves
                    // the peer did not object, and a peer that never asked objects to nothing.
                    await ConfirmAnonymousClientIsRefusedAsync(
                            name,
                            declaredProfile,
                            declaredEndpoint,
                            observedAddress,
                            host,
                            port,
                            certificates,
                            timeout.Token)
                        .ConfigureAwait(false);
                }

                return new SecurityConfirmation(
                    name,
                    kind,
                    declaredProfile,
                    declaredEndpoint,
                    observedAddress,
                    observedProtocol,
                    clientCertificate is not null,
                    SecurityConfirmationLevel.AuthenticatedRoundTrip,
                    clientCertificate is not null
                        ? "the broker answered a Kafka ApiVersions request over this connection, and "
                          + "REFUSED the same request on a second connection presenting no client "
                          + "certificate — so it both accepted the declared client identity and "
                          + "requires one. Topic-level authorisation is enforced per request and is "
                          + "not confirmed here."
                        : "the broker answered a Kafka ApiVersions request over this connection. No "
                          + "client identity is declared for this profile, so none was accepted and "
                          + "none is claimed.");
            }

            // ── Transport confirmation, with the limit stated (option 2) ──────────────────────
            var (outcome, rejection) = await ReadForRejectionAsync(tls, timeout.Token).ConfigureAwait(false);
            if (outcome == PostHandshakeOutcome.Cancelled)
            {
                throw Failure(
                    name,
                    $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the TLS "
                    + $"handshake against {observedAddress} completed, but confirmation was cancelled "
                    + "or ran out of its per-target budget before it finished. Nothing was confirmed, "
                    + "so nothing is claimed.");
            }

            if (outcome == PostHandshakeOutcome.Rejected)
            {
                throw Failure(
                    name,
                    $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the TLS "
                    + $"handshake against {observedAddress} completed, but the peer then rejected the "
                    + $"connection: {rejection}. Under TLS 1.3 a server cannot refuse a client "
                    + "certificate during the handshake — it does so immediately afterwards, which is "
                    + "what this reports.");
            }

            return new SecurityConfirmation(
                name,
                kind,
                declaredProfile,
                declaredEndpoint,
                observedAddress,
                observedProtocol,
                clientCertificate is not null,
                SecurityConfirmationLevel.TransportConfirmed,
                clientCertificate is not null
                    ? "TLS and the server's trust chain are confirmed and the declared client "
                      + "certificate was configured on the handshake with no rejection raised. Whether "
                      + "the peer REQUESTED it, and whether it ACCEPTS it, is confirmed at first step "
                      + "execution, not here: this target's application protocol is not known from its "
                      + "declaration or from any step, so no application-layer exchange is available, "
                      + "and a completed TLS 1.3 handshake carries no acceptance signal."
                    : "TLS and the server's trust chain are confirmed. No client identity is declared "
                      + "for this profile.");
        }
        finally
        {
            tls?.Dispose();
            tcp.Dispose();
        }
    }

    /// <summary>
    /// Exchanges one Kafka <c>ApiVersions</c> request over the established TLS session — the
    /// application-layer round trip a peer that refused the presented client certificate cannot
    /// complete (REQ-005, option 1).
    /// </summary>
    /// <remarks>
    /// Hand-framed rather than driven through <c>Confluent.Kafka</c> deliberately, for three
    /// reasons, all measured: an <c>AdminClient</c> metadata call follows the broker's
    /// <c>advertised.listeners</c> and timed out after 10.2 s against a broker that had ALREADY
    /// accepted the client certificate (a deployment fault reported as a security one is exactly
    /// what REQ-018's unconditional exit must not fire on); a request is answered by the connected
    /// broker in tens of milliseconds; and the engine acquires no new package dependency to make a
    /// four-field request. ApiVersions v0 with request header v1 — api_key, api_version,
    /// correlation_id, client_id — is answered by every broker version in use.
    /// </remarks>
    private static async Task ConfirmKafkaRoundTripAsync(
        string name,
        string declaredProfile,
        string declaredEndpoint,
        string observedAddress,
        SslStream tls,
        CancellationToken cancellationToken)
    {
        try
        {
            await KafkaApiVersionsExchangeAsync(tls, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
                                       or EndOfStreamException
                                       or InvalidDataException
                                       or OperationCanceledException
                                       or SocketException)
        {
            // Measured, and the reason this branch is worth more than a completed handshake:
            //   no client certificate against ssl.client.auth=required
            //     -> IOException "The decryption operation failed" (the broker's post-handshake
            //        bad_certificate alert), 17 ms;
            //   a TLS endpoint that is not Kafka at all
            //     -> EndOfStreamException, 41 ms.
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the TLS "
                + $"handshake against {observedAddress} completed, but the broker did not answer a Kafka "
                + $"ApiVersions request over it: {Summarise(ex)}. A completed TLS 1.3 handshake does not "
                + "prove the client certificate was accepted — the server's refusal arrives after it — "
                + "so this exchange, together with the anonymous-client check that follows it, is what "
                + "confirms authentication. Either the presented certificate was refused, or this "
                + "endpoint is not a Kafka broker.",
                ex);
        }
    }

    /// <summary>
    /// The differential arm (this file's header): opens a SECOND connection to the same address
    /// presenting NO client certificate and fails closed if it, too, completes the ApiVersions
    /// exchange — which means the peer does not require a client certificate at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only a SUCCESS is evidence here.</strong> Any failure of this arm is read as "the
    /// peer refused the anonymous client", and that asymmetry is deliberate: the first connection
    /// has already proved, moments earlier and against the same address, that the server's
    /// certificate satisfies the declared anchor and that the endpoint answers Kafka framing, so a
    /// failure here is attributable to the one thing that changed. A cancellation is the exception
    /// — it is not a refusal and is not read as one, because a probe that ran out of time confirmed
    /// nothing.
    /// </para>
    /// <para>
    /// The peer's own certificate is judged by the SAME callback the first connection used, so this
    /// arm cannot pass by relaxing trust.
    /// </para>
    /// </remarks>
    private static async Task ConfirmAnonymousClientIsRefusedAsync(
        string name,
        string declaredProfile,
        string declaredEndpoint,
        string observedAddress,
        string host,
        int port,
        ISecurityCertificateMaterial? certificates,
        CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        SslStream? tls = null;
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

            tls = new SslStream(
                tcp.GetStream(), leaveInnerStreamOpen: false, BuildValidationCallback(certificates));

            // Identical to the first connection except for the one variable under test: no
            // ClientCertificates collection is set, so the platform has nothing to present even if
            // the peer asks.
            await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = SslProtocols.None,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await KafkaApiVersionsExchangeAsync(tls, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the engine "
                + $"could not finish confirming that {observedAddress} REQUIRES a client certificate "
                + "before its per-target budget elapsed. That check is what separates enforced mutual "
                + "TLS from a broker that accepts everyone, so an unfinished one is reported rather "
                + "than assumed.",
                ex);
        }
        catch (Exception ex) when (ex is SocketException
                                       or AuthenticationException
                                       or IOException
                                       or EndOfStreamException
                                       or InvalidDataException)
        {
            // The outcome this arm is looking for: the peer refused a client with no identity.
            return;
        }
        finally
        {
            tls?.Dispose();
            tcp.Dispose();
        }

        throw Failure(
            name,
            $"declared profile 'mtls' on endpoint '{declaredEndpoint}' and {observedAddress} answered "
            + "a Kafka ApiVersions request with the declared client certificate — but it answered the "
            + "SAME request on a second connection presenting NO client certificate. The endpoint "
            + "therefore does not require a client certificate, so this is TLS wearing the word "
            + "'mutual': every step would run, every assertion would pass, and no client identity "
            + "would have been authenticated. Kafka's `ssl.client.auth` defaults to `none` and its "
            + "`requested` setting behaves the same way for this purpose — set it to `required` on "
            + "the broker's SSL listener, or declare `profile: tls` if server-only TLS is genuinely "
            + "what this endpoint provides.");
    }

    /// <summary>
    /// Writes one Kafka <c>ApiVersions</c> request over an established session and reads its
    /// response, returning normally only on a correctly-framed reply whose <c>error_code</c> is 0.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The reply was not correctly framed, echoed a different correlation id, or carried a non-zero
    /// <c>error_code</c>.
    /// </exception>
    private static async Task KafkaApiVersionsExchangeAsync(SslStream tls, CancellationToken cancellationToken)
    {
        const int CorrelationId = 1;
        var request = BuildApiVersionsRequest(CorrelationId);

        await tls.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await tls.FlushAsync(cancellationToken).ConfigureAwait(false);

        var sizeBuffer = new byte[4];
        await tls.ReadExactlyAsync(sizeBuffer, cancellationToken).ConfigureAwait(false);
        var size = ReadInt32BigEndian(sizeBuffer, 0);

        // A Kafka response is at minimum a correlation id plus an error code. Bounded so a
        // hostile or simply non-Kafka peer cannot induce a large allocation from a size field
        // this method has not yet earned the right to trust.
        if (size is < 6 or > (1 << 20))
        {
            throw new InvalidDataException(
                FormattableString.Invariant($"implausible Kafka response length {size}"));
        }

        var body = new byte[size];
        await tls.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        var correlation = ReadInt32BigEndian(body, 0);
        if (correlation != CorrelationId)
        {
            throw new InvalidDataException(
                FormattableString.Invariant(
                    $"correlation id {correlation} does not match the request's {CorrelationId}"));
        }

        // The field the whole exchange turns on, and the one this method exists to stop skipping:
        // a correctly-framed reply carrying `error_code = 58` (SASL_AUTHENTICATION_FAILED) or 35
        // (UNSUPPORTED_VERSION) is a REFUSAL, and reading only the correlation id would score it as
        // a successful round trip. Note the related trap this does NOT close: a `SASL_SSL` listener
        // answers ApiVersions BEFORE authentication by protocol design, so an `endpoint` selector
        // naming a SASL_SSL port reaches this same "errorCode=0" outcome having authenticated
        // nothing — the same class of gap the anonymous-client differential closes for `ssl.client.
        // auth`, arriving through another door. SASL is out of scope at 1.0 (no profile wires it).
        var errorCode = ReadInt16BigEndian(body, 4);
        if (errorCode != 0)
        {
            throw new InvalidDataException(
                FormattableString.Invariant(
                    $"the broker answered with Kafka error_code {errorCode} rather than 0"));
        }
    }

    /// <summary>
    /// Frames one Kafka <c>ApiVersions</c> v0 request: a big-endian length prefix, then request
    /// header v1 (<c>api_key</c>, <c>api_version</c>, <c>correlation_id</c>, <c>client_id</c>) and
    /// an empty body.
    /// </summary>
    /// <remarks>
    /// Separate from its async caller because <see cref="Span{T}"/> locals are not permitted in an
    /// async method on this language version — not a stylistic split.
    /// </remarks>
    private static byte[] BuildApiVersionsRequest(int correlationId)
    {
        var clientId = Encoding.UTF8.GetBytes("vouchfx-security-probe");
        var payloadLength = 2 + 2 + 4 + 2 + clientId.Length;
        var request = new byte[4 + payloadLength];
        var span = request.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span[..4], payloadLength);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(4, 2), ApiVersionsApiKey);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(6, 2), 0);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(8, 4), correlationId);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(12, 2), (short)clientId.Length);
        clientId.CopyTo(span[14..]);
        return request;
    }

    /// <summary>
    /// Reads a big-endian <see cref="int"/> at <paramref name="offset"/>. Separate from its async
    /// caller for the same <see cref="Span{T}"/>-in-async reason as
    /// <see cref="BuildApiVersionsRequest"/>.
    /// </summary>
    private static int ReadInt32BigEndian(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));

    /// <summary>
    /// Reads a big-endian <see cref="short"/> at <paramref name="offset"/>, for the same
    /// <see cref="Span{T}"/>-in-async reason as <see cref="ReadInt32BigEndian"/>.
    /// </summary>
    private static short ReadInt16BigEndian(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(offset, 2));

    /// <summary>
    /// What a bounded post-handshake read observed. Four outcomes, kept apart because collapsing
    /// any two of them is how a rejection gets reported as a confirmation.
    /// </summary>
    private enum PostHandshakeOutcome
    {
        /// <summary>The grace window elapsed with the connection open and silent.</summary>
        Quiet,

        /// <summary>The peer sent bytes — a server-speaks-first protocol greeted us.</summary>
        ServerGreeting,

        /// <summary>The peer refused: an error, or a clean close (a 0-byte read is EOF).</summary>
        Rejected,

        /// <summary>
        /// The caller's own token cancelled, or the per-target budget elapsed. Nothing was
        /// observed, so nothing may be concluded either way.
        /// </summary>
        Cancelled,
    }

    /// <summary>
    /// Waits a bounded moment for an explicit rejection after a completed handshake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a rejection detector, never an acceptance signal, and the difference is
    /// measured.</strong> Against nginx with <c>ssl_verify_client on</c>, a connection WITH a valid
    /// client certificate and one WITHOUT read identically — <c>TIMED-OUT after ~4000 ms</c> in
    /// both arms — because nginx rejects at the HTTP layer, not the TLS layer. So a quiet
    /// connection means only "no refusal was raised", and the caller reports exactly that.
    /// </para>
    /// <para>
    /// <strong>Three distinct things can end the read, and the byte count is what tells them
    /// apart.</strong> A THROW is a refusal — which is what a Kafka broker does, <c>IOException</c>
    /// within 17 ms. A <c>0</c>-byte return is ALSO a refusal: it is EOF, meaning the peer closed
    /// the connection, and that is the commonest shape of all. The nginx arm that motivated this
    /// method happens to TIME OUT, but HAProxy, Envoy and a Java server with <c>needClientAuth</c>
    /// all CLOSE — discarding the count would score every one of them as "no rejection raised".
    /// A count <c>&gt; 0</c> is the server-greeting case and is positive evidence the session is
    /// live.
    /// </para>
    /// </remarks>
    private static async Task<(PostHandshakeOutcome Outcome, string? Detail)> ReadForRejectionAsync(
        SslStream tls, CancellationToken cancellationToken)
    {
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(RejectionGrace);

        var buffer = new byte[1];
        try
        {
            var read = await tls.ReadAsync(buffer.AsMemory(), grace.Token).ConfigureAwait(false);
            return read == 0
                ? (PostHandshakeOutcome.Rejected,
                    "the peer closed the connection immediately after the handshake (a 0-byte read is "
                    + "end-of-stream, not silence)")
                : (PostHandshakeOutcome.ServerGreeting, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // ONLY the grace window elapsed — the connection is open and silent, the overwhelmingly
            // common shape, since most protocols wait for the client to speak first. The filter
            // tests the CALLER's token rather than `grace`'s (which is linked, so it reports
            // cancelled either way), which is what keeps a per-target-budget expiry out of here.
            return (PostHandshakeOutcome.Quiet, null);
        }
        catch (OperationCanceledException)
        {
            // The caller's token cancelled, or its per-target budget elapsed. This method observed
            // nothing, and "nothing observed" must not read as "nothing wrong": REQ-018 keys its
            // unconditional non-zero exit on the failure this would otherwise swallow.
            return (PostHandshakeOutcome.Cancelled, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            return (PostHandshakeOutcome.Rejected, Summarise(ex));
        }
    }

    /// <summary>
    /// Builds the server-certificate validation callback for this target: the declared trust
    /// anchor's own verdict when a <c>caCert</c> is declared, and the platform's own otherwise.
    /// </summary>
    /// <remarks>
    /// The two branches mirror <c>Security_Helpers.ConfigureHandler</c> exactly, and must: a probe
    /// that judged the peer by a different rule than the step would could pass a topology the step
    /// then rejects, or — far worse — pass one the step should have rejected. With no
    /// <c>caCert</c> declared the engine installs no callback at all and the platform's verdict
    /// stands unchanged; with one declared it is a PIN, consulted on every outcome including
    /// <see cref="SslPolicyErrors.None"/>.
    /// </remarks>
    private static RemoteCertificateValidationCallback? BuildValidationCallback(
        ISecurityCertificateMaterial? certificates)
    {
        if (certificates?.CaCertificatePath is null)
        {
            return null;
        }

        return (_, certificate, chain, errors) =>
        {
            // SslStream's callback hands back the base X509Certificate while the material's
            // decision takes an X509Certificate2. In practice the platform passes an
            // X509Certificate2 already, so the common path allocates nothing; the copy is a
            // fallback for a runtime that does not, and it is DISPOSED — this callback runs once
            // per handshake and an undisposed certificate here would hold an unmanaged handle for
            // the life of the run.
            if (certificate is X509Certificate2 typed)
            {
                return certificates.TrustsRemoteCertificate(typed, chain, errors);
            }

            if (certificate is null)
            {
                return certificates.TrustsRemoteCertificate(null, chain, errors);
            }

            using var copy = new X509Certificate2(certificate);
            return certificates.TrustsRemoteCertificate(copy, chain, errors);
        };
    }

    private static ISecurityConfiguration? ResolveConfiguration(
        string name,
        string declaredProfile,
        string declaredEndpoint,
        string observedAddress,
        ISecurityConfigurationAccessor security)
    {
        try
        {
            return security.For(name);
        }
        catch (SecurityMaterialException ex)
        {
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}' (reached at "
                + $"{observedAddress}), but its security configuration could not be resolved: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// True for a target the probe knows how to speak to at the application layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources, and the second is the one that matters for the deployment this feature exists
    /// for. The declared dependency <c>type</c> states the protocol directly. The suite's own STEPS
    /// state it just as certainly: if a <c>mq-publish.kafka</c> or <c>mq-expect.kafka</c> step names
    /// this target, the suite is about to speak Kafka to it, whatever kind it was declared as.
    /// </para>
    /// <para>
    /// <strong>Why the step list rather than the declaration kind alone.</strong> REQ-011 states
    /// outright that the customer's mTLS broker "runs its own entrypoint/config and is authored as a
    /// SERVICE, not the engine-provisioned <c>kafka</c> dependency type" — so keying only on the
    /// kind hands the strong confirmation to the shape the customer does not use and the weak one to
    /// the shape they do. The probe's job is to confirm what the steps are about to do, and the
    /// suite already carries that fact; requiring the author to restate it as a schema field would
    /// add freeze-critical surface for something already inferable.
    /// </para>
    /// <para>
    /// This never GUESSES. A service with no Kafka step targeting it stays on the transport-only
    /// branch and says so, exactly as before — sending Kafka framing into an HTTP connection on the
    /// chance it might be a broker would prove nothing and corrupt the connection while doing it.
    /// </para>
    /// </remarks>
    private static bool SpeaksKafka(string name, string kind, IReadOnlySet<string> kafkaSpeakingTargets) =>
        string.Equals(kind, "kafka", StringComparison.Ordinal) || kafkaSpeakingTargets.Contains(name);

    /// <summary>
    /// Resolves the host-side address the engine can actually reach a declared target at, from
    /// the values the topology staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SERVICE declaring <c>security</c> stages an <c>https://host:port</c> URL for its SECURED
    /// endpoint (REQ-023), so the address is read straight off that URI — the probe and the step
    /// therefore reach the same endpoint by construction.
    /// </para>
    /// <para>
    /// A DEPENDENCY stages a connection string; for kafka that is a bare <c>host:port</c>
    /// bootstrap. <strong>Note what this means, because it is the one place a declared
    /// <c>security.endpoint</c> and the probed address can legitimately differ.</strong>
    /// <c>security.endpoint</c> names a CONTAINER-side port, while the value staged here is the
    /// HOST-side published address for the dependency's own endpoint. Nothing in this release
    /// constructs a second, secured endpoint for a dependency the way REQ-023 does for a service —
    /// measured: <c>EnvironmentMapper</c> never reads a dependency's <c>Security</c> — so a
    /// dependency whose secured listener is on a container port other than the one its Aspire
    /// resource targets is not reachable from the engine at all, and this probe fails closed
    /// against whatever IS published. That is the correct outcome (a step would fail the same
    /// way), and the diagnostic names both halves so the cause is legible.
    /// </para>
    /// </remarks>
    private static bool TryResolveAddress(
        string name, IReadOnlyDictionary<string, object> discoveredServices, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (!discoveredServices.TryGetValue(name, out var staged) || staged is not string value ||
            string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Port > 0 &&
            !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host;
            port = uri.Port;
            return true;
        }

        var separator = value.LastIndexOf(':');
        if (separator > 0 && separator < value.Length - 1 &&
            int.TryParse(
                value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is >= 1 and <= 65535)
        {
            host = value[..separator];
            port = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the <see cref="OrchestrationException"/> every failure path here raises: kind
    /// <see cref="OrchestrationErrorKind.SecurityConfirmation"/>, resource the declared target
    /// name, detail the declared-versus-observed sentence.
    /// </summary>
    private static OrchestrationException Failure(string name, string detail, Exception? inner = null) =>
        new(
            new OrchestrationErrorInfo(
                Kind: OrchestrationErrorKind.SecurityConfirmation,
                ResourceName: name,
                RegistryHost: null,
                AuthStatus: null,
                Detail: $"'{name}' {detail}"),
            inner);

    /// <summary>
    /// Collapses an exception to a single line, preferring the innermost message — the platform
    /// wraps a TLS alert two or three layers deep and the outer text says only "see inner
    /// exception".
    /// </summary>
    private static string Summarise(Exception exception)
    {
        var current = exception;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }

        var message = current.Message.ReplaceLineEndings(" ").Trim();
        return message.Length > 200 ? message[..200] : message;
    }
}
