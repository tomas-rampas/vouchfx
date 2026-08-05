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
// AND THE ASYMMETRY STOPS AT THE CONNECT (MAJOR-1, peer review fix round three). "A failure of the
// second arm is attributable to the one thing that changed" holds for the TLS and application
// phases and FAILS for the connect phase, because the connect is not the thing that changed. A
// refused connection, a reset, a full backlog, an exhausted ephemeral-port range or a container
// restarting between the two arms all happen before any TLS and carry no information about
// `ssl.client.auth` — yet each was being scored as a certificate refusal, so the probe could
// announce that a broker "requires an identity" on the strength of a connection being refused. The
// connect now has its own try and its own outcome: UNFINISHED, not refusal, failing closed in the
// same direction cancellation already takes. See ConfirmAnonymousClientIsRefusedAsync's remarks for
// the measured exception-type table that fixed the evidence filter.
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
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
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
    /// Every <c>security.profile</c> this probe knows how to reason about, and — for each — whether
    /// it presents a CLIENT IDENTITY the peer could accept or refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// m3 (peer review, fix round three). This was a bare <c>"mtls"</c> literal compared inline. It
    /// degrades safely — an unrecognised profile takes the no-client-identity path — but the
    /// degradation is exactly the wrong one for THIS component: a future registered profile that
    /// DOES present a client identity (a SASL mechanism, a bearer token, Kerberos) would silently
    /// skip both the certificate resolution and the anonymous-client differential, and the
    /// differential is the one control in this feature whose entire job is preventing a false pass.
    /// Both emitted helpers are already pinned to the registry by
    /// <c>SecurityProfileRegistryTests.EveryWiredProfile_IsNamedInTheEmittedHelpersProfileSwitch</c>;
    /// this dictionary is what lets the same drift guard cover the probe.
    /// </para>
    /// <para>
    /// It is an EXHAUSTIVE map rather than a set of identity-presenting profiles on purpose: a set
    /// cannot tell "this profile presents nothing" from "this probe has never heard of this
    /// profile", and only the second is a drift signal.
    /// </para>
    /// <para>
    /// m3 (fix round four) wired that third answer through to BEHAVIOUR, which round three left
    /// undone: the call site folded "absent" into "presents nothing" via a
    /// <c>TryGetValue(…) &amp;&amp; presents</c> short-circuit, so an unrecognised profile silently
    /// took the no-client-identity path. <see cref="ConfirmOneAsync"/> now refuses it. The
    /// build-time half — this map's key set pinned to the registry's, so the drift turns a build
    /// red when the profile is ADDED rather than when a suite meets it — is
    /// <c>SecurityProfileVocabularyDriftTests</c>, which round three already put in place; the two
    /// halves are complementary, not alternatives.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, bool> ProfilesPresentingAClientIdentity =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["tls"] = false,
            ["mtls"] = true,
        };

    /// <summary>
    /// The recognised profiles, rendered for an author-facing diagnostic — derived from
    /// <see cref="ProfilesPresentingAClientIdentity"/> rather than restated, so a profile added to
    /// that map cannot leave the message naming a vocabulary the engine no longer has.
    /// </summary>
    private static readonly string SupportedProfiles =
        string.Join(", ", ProfilesPresentingAClientIdentity.Keys.OrderBy(p => p, StringComparer.Ordinal));

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

        // m5 (fix round three): the walk itself is SecuredTargets.Enumerate — the single spelling
        // shared with EnvironmentSecurityValidator's cheap "does this suite claim anything" check
        // and SuiteTopology's no-accessor guard, so the three cannot drift about which targets are
        // secured or in what order they are reported.
        foreach (var (name, kind, spec) in SecuredTargets.Enumerate(environment))
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

        // m3 (security review, fix round four): the map's third answer is USED. TryGetValue
        // returning false means "this probe has never heard of this profile" — a distinct fact from
        // ["tls"] = false, which is why this is an exhaustive map rather than a set of
        // identity-presenting profiles (see ProfilesPresentingAClientIdentity's own remarks). Folded
        // into the same short-circuit, that distinction was recorded and then discarded: an
        // unrecognised profile took the no-client-identity path and the run continued, confirming at
        // the transport-only level a target whose declared profile may well have demanded more.
        //
        // Refused instead, before any socket work. This is fail-closed in the one component whose
        // job is preventing a false pass: a profile the probe cannot reason about is a profile whose
        // anonymous-client differential — the single control separating "the broker accepted THIS
        // identity" from "the broker accepts anybody" — cannot be run, and a confirmation that
        // silently skips it is worth less than no confirmation at all.
        //
        // FIRST, before the address is even resolved (m7, security review, fix round five). It
        // depends on nothing but the declared profile, and running it after TryResolveAddress meant
        // an unrecognised profile on a target with no staged address reported "staged no reachable
        // address" — true, but not the fact that matters. Both orderings fail closed; this one names
        // the cause. Free to move: no measurement below feeds it.
        //
        // REACHABILITY, corrected by measurement (MAJOR-2, fix round five; the previous note here
        // claimed this throw was "UNREACHABLE BY AN AUTHOR TODAY", which is false on one path):
        //
        //   • The engine's profile VOCABULARY is closed at build time, and by a stronger route than
        //     the schema. SecurityProfileRegistry and SecurityProfileWiringAttribute are `internal`
        //     to Vouchfx.Engine.Compilation, whose InternalsVisibleTo names exactly
        //     Vouchfx.Engine.Runtime plus two test assemblies — measured, in that project's csproj.
        //     No out-of-tree assembly can contribute a profile at all, so the only way the registry
        //     and this map disagree is an in-tree edit, and SecurityProfileVocabularyDriftTests
        //     turns that into a red build at the moment of registration.
        //
        //   • On `vouchfx run` (single-scenario, shared-topology and --parallel alike) an author
        //     cannot reach this throw: the root schema narrows `profile` to the registered set
        //     (REQ-021) and SecurityProfileWiringValidator rejects an unresolved (profile,
        //     target-kind) pair (REQ-022), both inside ProviderPipeline.Compile, which runs before
        //     the topology is built.
        //
        //   • On `--watch` an author CAN. The watch compile seam (WatchRunner.Compile) is
        //     YamlDocumentParser.Parse + AstBuilder.Build only — no DocumentValidator.Validate, no
        //     ProviderPipeline.Compile — so `profile: kerbros` reaches the probe, and the run has
        //     already started containers and passed the health gate by then. That is why the message
        //     below is written for the AUTHOR, naming the profiles this engine actually has, with
        //     the engine-maintainer instruction demoted to a parenthesis.
        if (!ProfilesPresentingAClientIdentity.TryGetValue(declaredProfile, out var presentsClientIdentity))
        {
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}', which is "
                + $"not a security profile this engine recognises. Supported profiles: {SupportedProfiles}. "
                + "If that is a typo, correct it; nothing about this target was confirmed. The probe "
                + "cannot tell whether an unknown profile is meant to present a client identity, so "
                + "it cannot run the anonymous-client differential that separates 'the peer accepted "
                + "this identity' from 'the peer accepts anybody' (REQ-005), and confirming at the "
                + "transport-only level instead would report assurance it never obtained. (Engine "
                + "maintainers: a profile added to SecurityProfileRegistry must also be added to "
                + "SecuredEndpointProbe.ProfilesPresentingAClientIdentity.)");
        }

        if (!TryResolveAddress(name, discoveredServices, out var host, out var port))
        {
            throw Failure(
                name,
                $"declared security (profile '{declaredProfile}', endpoint '{declaredEndpoint}') but the "
                + $"topology staged no reachable address for '{name}', so there is nothing to confirm. "
                + "A secured target must be one the engine itself starts and publishes an endpoint for.");
        }

        var observedAddress = AuthorityText.Format(host, port);
        var configuration = ResolveConfiguration(name, declaredProfile, declaredEndpoint, observedAddress, security);
        var certificates = configuration?.Certificates;

        // The SAME material a step will use, resolved through the SAME accessor — which is what
        // makes a probe pass evidence about the step rather than about the probe. Reading these
        // properties re-checks path containment (REQ-003) and loads the files, so a declared
        // certificate that exists but is malformed surfaces HERE, before any container work is
        // wasted, naming the declared path.
        X509Certificate2? clientCertificate = null;

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
                    $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}', but its "
                    + $"client identity could not be loaded: {ex.Message}",
                    ex);
            }

            if (clientCertificate is null)
            {
                throw Failure(
                    name,
                    $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}', but no "
                    + "'clientCert'/'clientKey' pair resolved for it. Mutual TLS with no client identity "
                    + "is an unauthenticated connection wearing the word 'mutual'.");
            }
        }

        // The TRUST ANCHOR, loaded here for the same reason the client identity above is — and this
        // one was a DEFECT until fix round eight (B1, peer-review critic), not merely a tidiness.
        //
        // BuildValidationCallback reads only CaCertificatePath (containment, no load), so the anchor
        // used to load LAZILY on the first line of TrustsRemoteCertificate — i.e. INSIDE the
        // RemoteCertificateValidationCallback, during AuthenticateAsClientAsync.
        //
        // MEASURED on .NET 8.0.29 / Windows 11 with a throwing callback against an in-process TLS
        // listener: an exception thrown from that callback propagates RAW out of
        // AuthenticateAsClientAsync — NOT AuthenticationException, NOT IOException. (An IOException
        // thrown from the callback does arrive as IOException, and a callback RETURNING false gives
        // AuthenticationException; those are the two shapes that made this invisible.)
        // SecurityMaterialException derives directly from Exception, so it matched neither evidence
        // filter — not the handshake filter below, not the anonymous arm's — escaped ConfirmAsync,
        // was rethrown raw by SuiteTopology's dispose-and-throw catch, passed ScenarioRunner's
        // ArgumentException/OrchestrationException seams and reached a RunCommand with no broad
        // catch: a stack trace instead of a verdict, with no report artefacts and a non-taxonomy
        // exit code.
        //
        // WHAT THIS FIX CLOSES, AND WHAT IT DOES NOT (m-B1-scope, gatekeeper, fix round nine;
        // scoped by measurement in the slice F hygiene pass, which found the artefact claim true of
        // one runner path and false of the other). Classifying the failure closes two of those
        // three on every path: the run yields a verdict, and REQ-018's taxonomy exit code, instead
        // of a stack trace. The artefact gap is where the two runner paths diverge, and saying
        // otherwise would be the same over-claim this file exists to prevent.
        //
        //   • SHARED-TOPOLOGY `run` — the CLI default — writes no JUnit, HTML or events file EVEN
        //     WHEN THEY WERE REQUESTED, which is the real differential: the paths are set and
        //     nothing arrives. The classified failure lands in the `catch (OrchestrationException)`
        //     attached to ScenarioRunner.RunSuiteAsync's StartAsync try, which returns a
        //     SuiteResult built right there — carrying the aggregate verdict, a per-scenario verdict
        //     list and the security flag, but NO event buffer — ahead of that method's own
        //     TerminalRenderer.Render and FileReportWriter.WriteFileReports. (RunSuiteAsync's
        //     all-early-verdict guard delegates to CompleteWithoutTopologyAsync, which renders and
        //     writes reports of its own; a probe failure can never reach it, because reaching the
        //     probe means the topology was being built and so not every scenario had an early
        //     verdict.) The operator sees that catch's own diagnostic line ("RunSuiteAsync:
        //     topology failed to start — …"), not rendered events.
        //
        //   • `--parallel` DOES write whichever of them were requested — FileReportWriter guards
        //     each artefact on its own path being non-empty, so this is a differential about
        //     content, not about turning reporting on. Each scenario owns its topology there, so
        //     the same classified failure is caught in
        //     ScenarioRunner.RunScenarioOwningTopologyAsync's `catch (OrchestrationException)`,
        //     which buffers three lines: ScenarioStarted, the environment-error line built by the
        //     EnvironmentErrorEvents helper's ToLine, and ScenarioCompleted. ParallelSuiteRunner
        //     concatenates every slot's buffer and calls FileReportWriter.WriteFileReports over it.
        //
        // Issue #373 tracks the shared-topology gap, shared with the divergence guard: a suite
        // failing here gets PARITY with every other probe failure on that path, not artefacts none
        // of them produce.
        //
        // Reachable by ordinary authoring error, which is what makes it a blocker rather than a
        // theoretical one: REQ-004's preflight checks containment and File.Exists and never parses,
        // so an empty file, a mangled PEM, a private key pasted where the certificate belongs, or a
        // file the runner cannot read all pass preflight and used to detonate in the handshake.
        // "Right filename, wrong bytes" is routine in a bank PKI rollout.
        //
        // BOTH PROFILES, deliberately: a `tls` probe verifies the peer's chain against the declared
        // anchor exactly as an `mtls` one does, so it loads the anchor on the same path.
        //
        // It also keeps the diagnostic honest. Summarise() takes the INNERMOST message, which for
        // the escaping load is the platform's own — free to name the RESOLVED path, against this
        // branch's declared-path-only rule for anything archived into the §14 stream.
        // SecurityMaterialException's own message names the DECLARED path, and reporting it from
        // here is what puts it in front of Summarise rather than behind it.
        //
        // THE ANONYMOUS ARM NEEDS NOTHING FURTHER, and that is a property of the accessor rather
        // than an assumption: the production material memoises the anchor in a
        // Lazy<X509Certificate2?>(ExecutionAndPublication), so once this load succeeds every later
        // read — including the second connection's callback — returns the cached instance and
        // cannot re-enter the loader even if the file changes underneath the run. Measured by
        // SecurityConfigurationAccessorTests.For_ResolvedTwice_ReturnsTheSameCertificateInstances
        // (Assert.Same over both certificate views). The CaCertificatePath view is not memoised,
        // but it is a pure path computation whose containment verdict cannot change between two
        // calls in the same probe.
        //
        // B1's SIBLING — the other traveller on this same raw-escape route — IS NOW CLOSED, and
        // not here. X509Chain.Build inside TrustsRemoteCertificate, and the X509Certificate2 copy
        // fallback in BuildValidationCallback, can each throw a CryptographicException from inside
        // the validation callback; both were escaping exactly as the load above used to.
        //
        // NOT HYPOTHETICAL — stated precisely because the obvious wording ("has never been
        // observed") was written here first and was wrong within the hour. `X509Chain.Build` was
        // MEASURED throwing "An unknown chain building error occurred." on this host during fix
        // round eight, against the two-tier test bed, reproducibly and outside any test host.
        //
        // WHERE THE CATCH LIVES: one per arm, each spanning the WHOLE of its arm's use of the
        // connection — the outer try below, and the body of ConfirmAnonymousClientIsRefusedAsync —
        // never at this call site and never inside TrustsRemoteCertificate. The reason is measured
        // rather than stylistic, and is recorded in full at the anonymous arm's catch: collapsing
        // the fault to a `false` return (the tidy source-level fix) makes the anonymous
        // connection's failure indistinguishable from a genuine refusal, and was measured awarding
        // AuthenticatedRoundTrip against a broker that required nothing. The two arms give the same
        // fault opposite meanings, so only the arms can classify it; a boolean cannot carry the
        // difference across that boundary.
        //
        // This block therefore stays about the LOAD, which is the fault it can name honestly.
        try
        {
            // The path view first (containment, REQ-003) and then the object view (the load), in
            // the order the callback itself would have touched them.
            _ = certificates?.CaCertificatePath;
            _ = certificates?.CaCertificate;
        }
        catch (SecurityMaterialException ex)
        {
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}', but its "
                + $"declared trust anchor could not be loaded: {ex.Message}",
                ex);
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
        catch (CryptographicException ex)
        {
            // B1's SIBLING (peer-review critic, fix round nine): the validation callback's own
            // machinery failing, as distinct from the peer failing it. MEASURED on .NET 8.0.29 /
            // Windows 10.0.26200, three runs, unanimous: a CryptographicException thrown from a
            // RemoteCertificateValidationCallback propagates RAW out of AuthenticateAsClientAsync
            // — not AuthenticationException, not IOException — so before this catch it took B1's
            // escape route out of the probe entirely (measured end to end through ConfirmAsync:
            // `is OrchestrationException: False`).
            //
            // Two reachable sources, both measured on this host: X509Chain.Build inside
            // TrustsRemoteCertificate ("An unknown chain building error occurred.", observed
            // during fix round eight against the two-tier bed — a polluted CurrentUser\CA store
            // is the known trigger; and "A null or disposed certificate was present in
            // CustomTrustStore." for a disposed anchor, with the throw confirmed to come from
            // Build and not from CustomTrustStore.Add), and the X509Certificate2 copy fallback in
            // BuildValidationCallback ("m_safeCertContext is an invalid handle." over a disposed
            // base certificate). Both take this same route, so both are classified here.
            //
            // A DISTINCT catch rather than `or CryptographicException` on the handshake filter
            // above, because that filter's message names two PEER-side causes ("may not be running
            // TLS at all", "its certificate may not satisfy the declared trust anchor") and this
            // fault is the test host's own. Reporting a local chain-builder failure as a verdict on
            // the peer is the same class of false detail this file spends its length refusing.
            //
            // ON THE OUTER TRY, NOT BESIDE AuthenticateAsClientAsync (the critic's residual, closed
            // in the slice F hygiene pass). This arm's catch used to wrap the handshake call ALONE
            // while the anonymous arm's wrapped connect + handshake + exchange, and the asymmetry
            // was not merely untidy: the validation callback can be re-entered AFTER the handshake
            // by a peer-initiated TLS 1.2 renegotiation, and neither door is shut here — the
            // options built above pin no protocol version (EnabledSslProtocols = SslProtocols.None)
            // so a 1.2 session is not excluded, and AllowRenegotiation was MEASURED defaulting to
            // True on the pinned runtime (net8.0, a bare SslClientAuthenticationOptions). A fault
            // raised during ConfirmKafkaRoundTripAsync or ReadForRejectionAsync therefore left by
            // B1's raw-escape route. No reachability was demonstrated — it needs a peer that
            // renegotiates mid-exchange AND a coincident host crypto fault — so this is symmetry
            // restored cheaply, not an observed defect fixed. Widening recaptures nothing that
            // already had a verdict: the anonymous arm converts its own cryptographic fault to an
            // OrchestrationException before returning here, which this catch does not match.
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the engine's "
                + "own certificate validation failed with a cryptographic fault while confirming "
                + $"{observedAddress}: {Summarise(ex)}. That is a fault in this host's certificate "
                + "handling — a stale or corrupt entry in the host's certificate stores is the usual "
                + "cause — and NOT a verdict on the peer, so nothing was confirmed and nothing is "
                + "claimed.",
                ex);
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
    /// <strong>Only a failure AT OR AFTER THE HANDSHAKE is evidence here</strong> — and the
    /// qualifier is the whole of MAJOR-1 (peer review, fix round three). The asymmetry is
    /// deliberate but it is narrower than "any failure of this arm": the first connection has
    /// already proved, moments earlier and against the same address, that the server's certificate
    /// satisfies the declared anchor and that the endpoint answers Kafka framing, so a failure
    /// <em>from the handshake onwards</em> is attributable to the one thing that changed — the
    /// absent client identity. The CONNECT is not the thing that changed. A connection refused, a
    /// reset, a full backlog, an exhausted ephemeral-port range or a container restarting between
    /// the two arms all happen BEFORE any TLS and carry no information whatever about
    /// <c>ssl.client.auth</c>; scoring one of them as a refusal would let this probe announce that
    /// a broker "requires an identity" on the strength of a connection being refused, which is the
    /// exact over-claim the differential exists to destroy, reached through a different door.
    /// </para>
    /// <para>
    /// So the connect gets its own <c>try</c> and its own outcome: <em>unfinished</em>, not
    /// refusal, failing closed in the same direction cancellation already takes.
    /// </para>
    /// <para>
    /// <strong>Which exception types remain refusal evidence, and the measurement that fixed the
    /// list.</strong> Measured on .NET 8 / SChannel against loopback listeners, one arm per row:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>ConnectAsync</c> to a port nothing listens on → a BARE <c>SocketException</c>. This
    ///     is the only arm that produces one.
    ///   </description></item>
    ///   <item><description>
    ///     peer resets DURING the handshake → <c>IOException</c> wrapping a <c>SocketException</c>
    ///     ("An existing connection was forcibly closed…"), never a bare <c>SocketException</c>.
    ///   </description></item>
    ///   <item><description>
    ///     no client certificate against a listener that requires one → <c>IOException</c> wrapping
    ///     a <c>SocketException</c> — the genuine refusal this arm is looking for.
    ///   </description></item>
    ///   <item><description>
    ///     peer resets mid application exchange — an ABORTIVE close (RST), i.e.
    ///     <c>LingerState = new LingerOption(true, 0)</c> then closing the raw
    ///     <see cref="System.Net.Sockets.Socket"/> → <c>IOException</c> wrapping a
    ///     <c>SocketException(ConnectionReset)</c>, "An existing connection was forcibly closed…".
    ///   </description></item>
    ///   <item><description>
    ///     peer closes cleanly after the handshake — a GRACEFUL close (FIN), i.e.
    ///     <c>Shutdown</c> then <c>Close</c> → <c>EndOfStreamException</c> (itself an
    ///     <c>IOException</c>), "Unable to read beyond the end of the stream."
    ///   </description></item>
    ///   <item><description>
    ///     peer DISPOSES its <c>SslStream</c> and nothing else — what a test double does →
    ///     <c>EndOfStreamException</c>, the same as the graceful row.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <strong>Abortive versus graceful is the whole difference between those rows, and it is
    /// spelled out because a review round disagreed about it — a disagreement now CLOSED by
    /// measurement rather than by hypothesis.</strong> The security review of fix round three
    /// reported the reset row as producing <c>EndOfStreamException</c>. Round four re-measured the
    /// abortive and graceful rows and got the two different results above; the security review of
    /// round four independently reproduced both and WITHDREW its finding. What round four could only
    /// offer as "the most likely reconciliation" — that a test double's close is a FIN — is now the
    /// third row, MEASURED on .NET 8.0.29 / Windows 10.0.26200, three runs, unanimous, by both that
    /// review and this one: a bare <c>SslStream.Dispose()</c> yields <c>EndOfStreamException</c>.
    /// That is the whole reconciliation; the rows were right and so was the original report, about
    /// different closes.
    /// </para>
    /// <para>
    /// <strong>How to reproduce the abortive row, because the obvious spelling does not.</strong>
    /// <c>LingerState = new LingerOption(true, 0)</c> followed by <c>TcpClient.Close()</c> produces
    /// <c>EndOfStreamException</c>, not a reset — measured, three runs — because
    /// <c>TcpClient.Close</c> disposes its <c>NetworkStream</c>, and <c>NetworkStream.Dispose</c>
    /// shuts the socket down BEFORE closing it, so a FIN goes out and the linger option never
    /// applies. Closing the raw <c>Socket</c> instead bypasses that shutdown and produces the RST.
    /// A measurement of "abortive" that closes the <c>TcpClient</c> is therefore measuring the
    /// graceful row under another name — the likeliest source of the original disagreement.
    /// </para>
    /// <para>
    /// Nothing in any of this changes the filter: every type named satisfies "is an
    /// <c>IOException</c>".
    /// </para>
    /// <para>
    /// <c>SocketException</c> is therefore DROPPED from the evidence filter — every post-connect
    /// socket fault arrives wrapped, so nothing that was genuine refusal evidence is lost, which is
    /// the risk the brief for this fix flagged. It is still CAUGHT, but as an unfinished
    /// confirmation rather than a refusal: should some platform surface a bare one after the
    /// connect, the safe reading is "nothing was confirmed" (a correct deployment aborts with a
    /// legible message) rather than "the peer refused" (a false security claim).
    /// <c>AuthenticationException</c> stays for a TLS-1.2-era listener that CAN fail the handshake
    /// synchronously; <c>InvalidDataException</c> stays because it is this file's own verdict on a
    /// reply that is not a clean <c>error_code = 0</c>.
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
            try
            {
                await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                throw Failure(
                    name,
                    $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the engine "
                    + $"could not open a second connection to {observedAddress} to confirm it REQUIRES a "
                    + $"client certificate: {Summarise(ex)}. That connection failed before any TLS, so it "
                    + "says nothing about whether the endpoint demands an identity — nothing was "
                    + "confirmed, so nothing is claimed.",
                    ex);
            }

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
        catch (CryptographicException ex)
        {
            // THE MEASUREMENT THAT CHOSE THIS SHAPE, and it overturned the obvious fix. The tidy
            // remedy for the raw escape is to catch at the source and return false from
            // TrustsRemoteCertificate — "a chain that cannot be BUILT is not a chain that is
            // TRUSTED". On the main arm that reads correctly. HERE it is a false security claim,
            // measured end to end against a PERMISSIVE loopback broker (ssl.client.auth=requested)
            // with the fault injected on the SECOND connection only:
            //
            //     callback returns false on connection 2
            //       -> AuthenticationException -> the refusal filter below -> return
            //       -> SecurityConfirmationLevel.AuthenticatedRoundTrip, "REFUSED the same request
            //          on a second connection presenting no client certificate — so it both
            //          accepted the declared client identity and requires one."
            //
            // The broker required nothing. A local chain-builder fault had been laundered into the
            // STRONGEST assurance this probe can award, on the arm whose entire purpose is to
            // destroy false assurance. That is strictly worse than the raw escape it would have
            // replaced: a crash is loud, a false green is not.
            //
            // So the exception is kept as the SIGNAL rather than collapsed into a boolean, and is
            // classified here — above the refusal filter, with the connect fault and the budget
            // expiry, in the "nothing was confirmed" direction. The distinction that must survive
            // is between "the peer rejected this connection" and "we could not judge the peer",
            // and `false` is precisely the value that erases it.
            //
            // The step-side consumer needs nothing equivalent: Security_Helpers installs the same
            // decision on an HttpClientHandler, and a CryptographicException from THAT callback was
            // measured arriving as HttpRequestException wrapping the cryptographic fault (three
            // runs) — already closed, already legible, and with no differential to over-claim on.
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the engine's own "
                + "certificate validation failed with a cryptographic fault on the second connection to "
                + $"{observedAddress}, before it could confirm that the endpoint REQUIRES a client "
                + $"certificate: {Summarise(ex)}. A fault in this host's certificate handling is not a "
                + "refusal by the peer, so nothing is claimed from it.",
                ex);
        }
        catch (SocketException ex)
        {
            // MEASURED unreachable on this stack — every post-connect socket fault arrives wrapped
            // in an IOException (see this method's remarks) — and deliberately NOT read as a
            // refusal if some other platform surfaces one. A bare socket fault is a transport
            // event, not a statement about `ssl.client.auth`.
            throw Failure(
                name,
                $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}'; the second "
                + $"connection to {observedAddress} failed at the socket layer while confirming it "
                + $"REQUIRES a client certificate: {Summarise(ex)}. A socket fault is not a "
                + "certificate refusal, so nothing is claimed from it.",
                ex);
        }
        catch (Exception ex) when (ex is AuthenticationException
                                       or IOException
                                       or EndOfStreamException
                                       or InvalidDataException)
        {
            // The outcome this arm is looking for: the peer refused a client with no identity.
            // Reached only from AuthenticateAsClientAsync onwards — the connect has its own catch
            // above — so "the one thing that changed" really is the absent client identity.
            // EndOfStreamException is an IOException and is named for the reader, not the compiler.
            return;
        }
        finally
        {
            tls?.Dispose();
            tcp.Dispose();
        }

        throw Failure(
            name,
            // m5 (spec-compliance review, fix round five): the profile is INTERPOLATED, not the
            // literal 'mtls' the round-four sweep left here while converting its three siblings.
            // Correct either way today — only mtls presents a client identity, so only mtls reaches
            // this method — but this sits on the path a future identity-presenting profile would
            // take, and a message naming a profile the author did not declare is the kind of
            // false detail that costs a reader an hour.
            $"declared profile '{declaredProfile}' on endpoint '{declaredEndpoint}' and {observedAddress} answered "
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
            //
            // The copy CAN throw — measured, `new X509Certificate2(X509Certificate)` over a disposed
            // base gives CryptographicException "m_safeCertContext is an invalid handle." — and it is
            // deliberately NOT guarded here. A CryptographicException raised anywhere in this lambda
            // (this copy, or TrustsRemoteCertificate's own chain build) leaves the callback by the
            // same route and is classified by whichever ARM drove the handshake, which is the only
            // place that knows whether "could not judge the peer" means a failed confirmation or a
            // withheld one. Guarding it here would have to invent a boolean answer and would erase
            // that distinction — see the anonymous arm's catch for the measurement.
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
    /// A SERVICE declaring <c>security</c> stages its SECURED endpoint (REQ-023), in the form that
    /// endpoint's own consumer uses: an <c>https://host:port</c> URL for a target the HTTP family
    /// addresses, and a bare <c>host:port</c> bootstrap authority for one the Kafka families
    /// address. Both shapes are handled below, and the probe and the step therefore reach the same
    /// endpoint by construction whichever it is.
    /// </para>
    /// <para>
    /// A DEPENDENCY stages a connection string; for kafka that is a bare <c>host:port</c>
    /// bootstrap. <strong>Note what this means, because it is the one place a declared
    /// <c>security.endpoint</c> and the probed address can legitimately differ.</strong>
    /// <c>security.endpoint</c> names a CONTAINER-side port, while the value staged here is the
    /// HOST-side published address for the dependency's own endpoint. Nothing in this release
    /// constructs a second, secured endpoint for a dependency the way REQ-023 does for a service —
    /// measured (m1, fix round three; the previous wording said "<c>EnvironmentMapper</c> never
    /// reads a dependency's <c>Security</c>", which this same commit falsified by adding REQ-016's
    /// <c>ServerArtifactInjection.Plan(spec.Security, "dependencies", …)</c> call): the mapper
    /// reads a dependency's <c>Security</c> only for its <c>serverArtifacts</c>, and constructs no
    /// ENDPOINT from it — every dependency endpoint still comes from its own Aspire registration.
    /// So a dependency whose secured listener is on a container port other than the one its Aspire
    /// resource targets is not reachable from the engine at all, and this probe fails closed
    /// against whatever IS published. That is the correct outcome (a step would fail the same
    /// way), and the diagnostic names both halves so the cause is legible.
    /// </para>
    /// <para>
    /// <strong>IPv6 (n7, fix round three; FIXED in fix round four).</strong> Both halves of this
    /// method used to mishandle an IPv6 authority. The URL half read <c>Uri.Host</c>, which retains
    /// the brackets (<c>https://[::1]:8443</c> → <c>[::1]</c>); it now reads
    /// <c>Uri.DnsSafeHost</c>. Earlier rounds justified that with a connect failure, and the
    /// justification was wrong — MEASURED on net8.0: <c>IPAddress.TryParse("[::1]")</c> returns
    /// <see langword="true"/> yielding <c>::1</c>, which the socket layer consults before it would
    /// build any DNS endpoint, and <c>TcpClient.ConnectAsync("[::1]", port)</c> against an IPv6
    /// loopback listener CONNECTED (4 ms, parameterless ctor; 0 ms, <c>InterNetworkV6</c> ctor). No
    /// lookup of the literal occurs. What the normalisation is actually for: the bracketed text is
    /// wrong in a rendered diagnostic, it is not the form the authority parser below yields, and
    /// .NET's leniency is not shared by every consumer of a value the engine hands on. The bare
    /// half split on <c>LastIndexOf(':')</c>, which is right for <c>host:port</c>, keeps the
    /// brackets on <c>[::1]:9093</c>, and is outright wrong for a bracketless form (<c>fe80::1</c>
    /// became host <c>fe80:</c> port <c>1</c> — a plausible address that is not the target); it now
    /// borrows the framework's own authority parser, per
    /// <see cref="TryParseBareAuthority"/>'s measured notes. The two old rows failed differently,
    /// and lumping them (as this note did) is what carried the refuted claim: the BRACKETED row
    /// did not fail at all — it connects, per the measurement above, so only its rendered message
    /// was wrong — while the BRACKETLESS row genuinely mis-split and failed CLOSED, producing a
    /// connect diagnostic against a fabricated port rather than a false confirmation. Nothing in
    /// this release stages an IPv6 authority either way (every host-published endpoint comes back
    /// as <c>localhost</c> or an IPv4 literal), so neither row changes a verdict reachable today.
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

        // A scheme-carrying URL — REQ-023's HTTP-family staging. DnsSafeHost, not Host: for
        // 'https://[::1]:8443' the framework reports Host='[::1]' (brackets retained, per RFC 3986)
        // and DnsSafeHost='::1'. Both MEASURED. Handing the bracketed text to TcpClient.ConnectAsync
        // does NOT break the connect — that consequence was asserted here for several rounds and is
        // false (see this method's remarks for the measurement). '::1' is taken because it is the
        // form the bare-authority half yields, the form a diagnostic should render, and the form
        // safe to hand to a consumer that does not share .NET's tolerance of the brackets.
        //
        // DELIBERATELY LAXER THAN THE BARE HALF (m3, gatekeeper, fix round five): this branch reads
        // host and port and ignores user info, path, query and fragment, where TryParseBareAuthority
        // refuses a value carrying any of them. That asymmetry is intended, not an oversight — a URL
        // legitimately carries a path, so refusing one would refuse a well-formed staged endpoint,
        // while a value with no scheme carrying a path is not an authority at all.
        //
        // The two halves of "ignored" are not the same, and an earlier wording lumped them (m2,
        // gatekeeper + security, fix round six). RFC 3986 §3.2 defines
        // `authority = [ userinfo "@" ] host [ ":" port ]`, so USER INFO IS PART OF THE AUTHORITY,
        // not something past it: it is discarded here by the URL parser itself, which yields host
        // and port as separate components and never folds user info into either. MEASURED, on the
        // pinned runtime: 'https://user@host.example:8443/p?q#f' gives DnsSafeHost 'host.example',
        // Port 8443 and UserInfo 'user' — three separate components, so reading the first two picks
        // up nothing of the third. Path, query and fragment are the components genuinely past the
        // authority, and they are ignored because nothing past the authority is consulted when
        // opening a TCP connection. Both roads lead to the same address; only the reasons differ.
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Port > 0 &&
            !string.IsNullOrEmpty(uri.DnsSafeHost))
        {
            host = uri.DnsSafeHost;
            port = uri.Port;
            return true;
        }

        // A bare 'host:port' authority — REQ-023's Kafka-family staging, and every dependency
        // connection string. Parsed by borrowing the framework's authority parser rather than by
        // hand (n7, fix round three, upgraded from "noted" to "fixed" by fix round four).
        return TryParseBareAuthority(value, out host, out port);
    }

    // The bracket rule this probe's observed-address rendering depends on (m1, gatekeeper, fix
    // round five) now lives in AuthorityText.Format — hoisted out of this file in fix round six
    // (m3) because EnvironmentMapper's TCP health-check diagnostics have the same bracket-free host
    // and had the same raw {host}:{port} defect. TryResolveAddress deliberately yields the
    // BRACKET-FREE host because that is what Uri.DnsSafeHost gives and what a consumer with no
    // obligation to match .NET's leniency can take — NOT because TcpClient.ConnectAsync requires
    // it, which is refuted (see TryResolveAddress's own remarks for the measurement). Everything
    // that renders it for a human goes through that one helper.

    /// <summary>
    /// The scheme synthesised in front of a bare <c>host:port</c> authority so
    /// <see cref="Uri"/> will parse it. Never appears in any output.
    /// </summary>
    private const string BareAuthorityScheme = "tcp://";

    /// <summary>
    /// Parses a bare <c>host:port</c> authority into its host and port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why not <c>Uri</c> directly, and why not a hand split.</strong> The obvious fix for
    /// the bracket handling below is <c>Uri.DnsSafeHost</c> — but MEASURED,
    /// <c>Uri.TryCreate("[::1]:9093", UriKind.Absolute, out _)</c> returns <see langword="false"/>:
    /// the framework will not parse an authority with no scheme. So a scheme is synthesised purely
    /// to reach the parser, and the result is validated to be exactly an authority and nothing
    /// else. What this replaced was a <c>LastIndexOf(':')</c> split, which is correct for
    /// <c>host:port</c> and wrong for every IPv6 spelling.
    /// </para>
    /// <para>
    /// <strong>MEASURED, on the pinned runtime, against the split it replaces</strong> (n7 recorded
    /// the first two as known-wrong-but-unreachable; the security review's fix round four resolved
    /// them rather than re-recording them):
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>[::1]:9093</c> — split gave host <c>[::1]</c>, brackets and all. That connects
    ///     anyway (measured; see <see cref="TryResolveAddress"/>'s remarks), so this row is a
    ///     normalisation, not a repair. Now <c>::1</c> / 9093.
    ///   </description></item>
    ///   <item><description>
    ///     <c>::1</c> and <c>fe80::1</c> (bracketless, no port) — split gave host <c>:</c> port 1
    ///     and host <c>fe80:</c> port 1 respectively: a plausible-looking address that is not the
    ///     declared target. Now rejected outright, which reports "staged no reachable address"
    ///     rather than a connect failure against a fabricated port.
    ///   </description></item>
    ///   <item><description>
    ///     <c>127.0.0.1:9093</c>, <c>localhost:9093</c>, <c>broker:9093</c> — unchanged, the shapes
    ///     every fabric in this release actually stages.
    ///   </description></item>
    ///   <item><description>
    ///     Port bounds now come from the parser: <c>host:70000</c> and <c>host:-1</c> fail to parse,
    ///     <c>host:65535</c> parses, and <c>host:0</c> parses with <c>Port == 0</c> and is rejected
    ///     below — the same 1–65535 window the explicit range check enforced.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Anything beyond a bare host and port is refused, not ignored — in THIS half.</strong>
    /// The rule is scoped to the bare-authority branch and does not govern the URL branch above it
    /// (m3, gatekeeper, fix round five): a staged value with no scheme is a host and a port and
    /// nothing else, so anything carrying user info, a path, a query or a fragment is not the thing
    /// this is parsing, and reinterpreting it would be a silent guess in the one component that must
    /// not guess. Note that user info is INSIDE the authority per RFC 3986 §3.2
    /// (<c>authority = [ userinfo "@" ] host [ ":" port ]</c>) while path, query and fragment are
    /// past it — the four are refused together here, but they are not one category (m2, fix round
    /// six). A staged URL legitimately carries a path, which is why that branch reads host and port
    /// off the parsed <see cref="Uri"/> and lets the rest be — see the call site's own note.
    /// Measured: <c>user@host:9093</c> parses with <c>UserInfo</c> set (the previous split
    /// produced the host <c>user@host</c>), <c>host:9093/path</c> keeps its path, and
    /// <c>host:9093#f</c> keeps its fragment while <c>PathAndQuery</c> still reads <c>/</c> — which
    /// is why the fragment is checked separately rather than folded into the path check.
    /// </para>
    /// <para>
    /// <strong>The user-info test is on the delimiter as well as on <c>UserInfo</c></strong>
    /// (m2 / NIT-1, security review, fix round five; wording corrected in round six — the
    /// <c>UserInfo.Length</c> check was RETAINED alongside the new <c>@</c> guard as belt and
    /// braces, not replaced by it, and the previous "not on <c>UserInfo</c>" phrasing read as if it
    /// had been). The rule above says "anything carrying user info"; asking
    /// <c>authority.UserInfo.Length != 0</c> ALONE enforced something narrower, because an EMPTY
    /// user-info component is still a user-info component. MEASURED, on the pinned runtime:
    /// <c>tcp://@host:9093</c> parses with <c>UserInfo</c> equal to <c>""</c>, <c>Host</c> equal to
    /// <c>host</c> and <c>Port</c> 9093 — so it passed every guard and resolved to a REACHABLE host
    /// that is not the text staged, where the split it replaced produced the unresolvable
    /// <c>@host</c>. Security's 74-value corpus found it to be the one input that retargets rather
    /// than merely failing differently. Nothing stages such a value today; the guard is tightened to
    /// the delimiter anyway, because this function decides what address a security probe connects
    /// to, and "no author can produce it" is a property of today's staging, not of this function.
    /// A <c>@</c> is illegal in a registered name, an IPv4 literal and an IPv6 literal alike, so
    /// refusing it outright narrows nothing legitimate.
    /// </para>
    /// </remarks>
    private static bool TryParseBareAuthority(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (value.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(BareAuthorityScheme + value, UriKind.Absolute, out var authority)
            || authority.Port <= 0
            || string.IsNullOrEmpty(authority.DnsSafeHost)
            || authority.UserInfo.Length != 0
            || authority.Fragment.Length != 0
            || !string.Equals(authority.PathAndQuery, "/", StringComparison.Ordinal))
        {
            return false;
        }

        host = authority.DnsSafeHost;
        port = authority.Port;
        return true;
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
