// REQ-005 / EDGE-004 / EDGE-005 / EDGE-010 — the fail-closed secured-confirmation probe
// (authenticated-infrastructure-mtls, slice E).
//
// Non-Docker. The probe takes a host, a port and the resolved security configuration, so it is
// exercised against ordinary loopback listeners — the same pattern EnvironmentMapperProbeAsyncTests
// already established here for the tcp health-check discriminator, and for the same reason: a
// discriminator whose only coverage runs in a continue-on-error Docker job can regress with no CI
// signal.
//
// WHAT EACH LISTENER STANDS IN FOR, because the point of this file is that these are DIFFERENT
// outcomes and a boolean cannot tell them apart:
//   • a plain TCP listener            → EDGE-004's plaintext port beside the secured one
//   • nothing listening at all        → EDGE-005's broker that came up with no SSL listener
//   • a TLS listener that says nothing→ a service: transport confirmed, acceptance NOT confirmed
//   • a TLS listener speaking Kafka   → a Kafka-speaking target: an authenticated round trip
//   • a TLS listener that is not Kafka→ a Kafka-speaking target: fails, the round trip is the proof
//   • a Kafka TLS listener that does NOT REQUIRE a client certificate → fails, and this is the arm
//     the whole differential exists for: everything else about it is green.
//
// THE LISTENERS ACCEPT MANY CONNECTIONS ON PURPOSE. For `profile: mtls` the probe opens a SECOND
// connection presenting no client certificate, so a one-shot listener would refuse it at the TCP
// layer and the differential would "pass" for the wrong reason — a false green in the test for the
// control that exists to prevent false greens. Every listener here therefore serves in a loop until
// stopped, and the mutual-TLS arms reject the anonymous connection at the TLS layer, the way a
// broker with `ssl.client.auth=required` does.
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// REQ-005 tests: what the probe confirms, what it refuses, and what it declines to claim.
/// </summary>
public sealed class SecuredEndpointProbeTests : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly TestCertificateBed _bed = TestCertificateAuthority.CreateSuiteDirectory();

    public void Dispose() => _bed.Dispose();

    // ── Environment / accessor fixtures ───────────────────────────────────────────────────

    private static EnvironmentSpec ServiceEnv(SecuritySpec security) =>
        new(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["sut"] = new ServiceSpec("acme/sut:1", null, null, null, null) { Security = security },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static EnvironmentSpec KafkaDependencyEnv(SecuritySpec security) =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySpec("kafka", null, null) { Security = security },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static SecuritySpec Profile(string profile) =>
        new(profile, "9093", null, null, null, null);

    private static Dictionary<string, object> Staged(string name, string host, int port) =>
        new(StringComparer.Ordinal) { [name] = $"{host}:{port}" };

    /// <summary>
    /// A hand-built <see cref="ISecurityConfigurationAccessor"/>. The production accessor is
    /// internal to <c>Vouchfx.Engine.Runtime</c>, and — more to the point — what these tests must
    /// pin is what the PROBE does with a configuration, not whether the accessor builds one
    /// correctly (slice D's <c>SecurityConfigurationAccessorTests</c> owns that).
    /// </summary>
    private sealed class FakeAccessor : ISecurityConfigurationAccessor
    {
        private readonly Dictionary<string, ISecurityConfiguration> _byTarget = new(StringComparer.Ordinal);

        internal FakeAccessor With(string target, string profile, FakeMaterial? material)
        {
            _byTarget[target] = new FakeConfiguration(profile, material);
            return this;
        }

        public ISecurityConfiguration? For(string targetName) =>
            _byTarget.TryGetValue(targetName, out var configuration) ? configuration : null;
    }

    private sealed record FakeConfiguration(string Profile, FakeMaterial? Material) : ISecurityConfiguration
    {
        ISecurityCertificateMaterial? ISecurityConfiguration.Certificates => Material;
    }

    private sealed class FakeMaterial : ISecurityCertificateMaterial
    {
        internal FakeMaterial(TestCertificateBed bed, bool withClientIdentity, bool declareCa, bool trusts = true)
        {
            CaCertificatePath = declareCa ? bed.CaPath : null;
            ClientCertificatePath = withClientIdentity ? bed.ClientCertPath : null;
            ClientKeyPath = withClientIdentity ? bed.ClientKeyPath : null;
            ClientCertificate = withClientIdentity ? LoadPresentableClientCertificate(bed) : null;
            CaCertificate = declareCa ? bed.CaCertificate : null;
            Trusts = trusts;
        }

        internal bool Trusts { get; }

        internal int TrustDecisions { get; private set; }

        public string? CaCertificatePath { get; }

        public string? ClientCertificatePath { get; }

        public string? ClientKeyPath { get; }

        public X509Certificate2? CaCertificate { get; }

        public X509Certificate2? ClientCertificate { get; }

        public bool TrustsRemoteCertificate(
            X509Certificate2? remoteCertificate, X509Chain? platformBuiltChain, SslPolicyErrors sslPolicyErrors)
        {
            TrustDecisions++;
            return Trusts;
        }

        /// <summary>
        /// Loads the bed's client certificate in a form SChannel will actually PRESENT, mirroring
        /// the production accessor's own PKCS#12 round trip.
        /// </summary>
        /// <remarks>
        /// Not ceremony, and this file measured it independently: built directly by
        /// <see cref="X509Certificate2.CreateFromPemFile(string, string)"/>, the certificate
        /// reports <c>HasPrivateKey = true</c> and then fails the client-authentication handshake
        /// with <c>Win32Exception: The credentials supplied to the package were not recognized</c>,
        /// because its key is EPHEMERAL and SChannel cannot use such a key for client auth. That
        /// is exactly the finding <c>SecurityConfigurationAccessor.LoadClient</c> records; a fake
        /// that skipped the round trip would make every mutual-TLS arm here fail at the handshake
        /// and prove nothing about the probe.
        /// </remarks>
        private static X509Certificate2 LoadPresentableClientCertificate(TestCertificateBed bed)
        {
            using var pemPair = X509Certificate2.CreateFromPemFile(bed.ClientCertPath, bed.ClientKeyPath);
            return new X509Certificate2(pemPair.Export(X509ContentType.Pkcs12));
        }
    }

    // ── Loopback listeners ────────────────────────────────────────────────────────────────

    /// <summary>
    /// How the test listener treats a client certificate — the three settings a real broker's
    /// <c>ssl.client.auth</c> takes, since which one is in force is exactly what REQ-005's
    /// differential measures.
    /// </summary>
    private enum ClientAuth
    {
        /// <summary>
        /// <c>ssl.client.auth=none</c>: no validation callback and no requirement. Measured on this
        /// platform, SChannel still sends a <c>CertificateRequest</c> in this mode and then applies
        /// its OWN default validation, which refuses this bed's private-CA client certificate — so
        /// it stands in for a permissive broker only on the ANONYMOUS arm, which is the arm that
        /// matters. <see cref="Requested"/> models the permissive broker on both arms.
        /// </summary>
        None,

        /// <summary>
        /// <c>ssl.client.auth=requested</c>: the certificate is requested and accepted when sent,
        /// and its ABSENCE is tolerated. This is the false-assurance shape — a green mutual-TLS
        /// suite against a broker that authenticates nobody.
        /// </summary>
        Requested,

        /// <summary>
        /// <c>ssl.client.auth=required</c>: requested, and a connection presenting none is refused.
        /// The only setting under which "mutual TLS" means anything.
        /// </summary>
        Required,
    }

    /// <summary>
    /// A loopback listener that serves connections in a LOOP until stopped, handing each to
    /// <paramref name="serve"/>. See this file's header for why one-shot would be wrong.
    /// </summary>
    private static (int Port, Task Serving, TcpListener Listener) Listen(Func<TcpClient, Task> serve)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serving = Task.Run(async () =>
        {
            var connections = new List<Task>();
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    connections.Add(Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                await serve(client).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // A refused/aborted connection is an expected outcome in the
                                // negative arms — including the deliberately-refused anonymous
                                // second connection of every enforcing mutual-TLS arm.
                            }
                        }
                    }));
                }
            }
            catch (Exception)
            {
                // listener.Stop() in the test's finally block ends the accept loop.
            }

            await Task.WhenAll(connections).ConfigureAwait(false);
        });

        return (port, serving, listener);
    }

    private (int Port, Task Serving, TcpListener Listener) ListenTls(
        Func<SslStream, Task> afterHandshake, ClientAuth clientAuth = ClientAuth.None)
        => Listen(async client =>
        {
            var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                var options = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _bed.ServerCertificate,
                    ClientCertificateRequired = clientAuth == ClientAuth.Required,
                };

                if (clientAuth != ClientAuth.None)
                {
                    // The SERVER side of a loopback test double: under `Requested` it accepts
                    // whatever the probe presents INCLUDING NOTHING (that is the whole point of the
                    // permissive arm); under `Required` it refuses a null certificate, which is what
                    // `ssl.client.auth=required` does. What is under test is the PROBE, never this
                    // listener's own trust decision, so no chain is built here. CA5359 flags this
                    // shape for production clients; this callback never leaves loopback.
#pragma warning disable CA5359
                    options.RemoteCertificateValidationCallback =
                        (_, certificate, _, _) => clientAuth != ClientAuth.Required || certificate is not null;
#pragma warning restore CA5359
                }

                await tls.AuthenticateAsServerAsync(options).ConfigureAwait(false);

                await afterHandshake(tls).ConfigureAwait(false);
            }
            finally
            {
                tls.Dispose();
            }
        });

    /// <summary>
    /// A loopback listener that accepts exactly ONE connection and then STOPS listening, so any
    /// further connection to that port is refused at the TCP layer.
    /// </summary>
    /// <remarks>
    /// The deliberate opposite of <see cref="Listen"/>, and the only place in this file that wants
    /// it (MAJOR-1, fix round three): it reproduces the shape the file header warns about — a
    /// first connection that succeeds completely, followed by a second that fails before any TLS
    /// — so the probe can be pinned to read that as UNCONFIRMED rather than as a certificate
    /// refusal. <c>Stop()</c> is called before the accepted connection is served, which does not
    /// affect it: an accepted socket is independent of the listening socket.
    /// </remarks>
    private (int Port, Task Serving, TcpListener Listener) ListenTlsOnce(
        Func<SslStream, Task> afterHandshake, ClientAuth clientAuth)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serving = Task.Run(async () =>
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The test's finally block stopped the listener before anything connected.
                return;
            }

            listener.Stop();

            using (client)
            {
                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _bed.ServerCertificate,
                        ClientCertificateRequired = clientAuth == ClientAuth.Required,
#pragma warning disable CA5359
                        RemoteCertificateValidationCallback = clientAuth == ClientAuth.None
                            ? null
                            : (_, certificate, _, _) =>
                                clientAuth != ClientAuth.Required || certificate is not null,
#pragma warning restore CA5359
                    }).ConfigureAwait(false);

                    await afterHandshake(tls).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Same tolerance as Listen: a refused or aborted connection is an expected
                    // outcome in the arms this helper serves.
                }
                finally
                {
                    tls.Dispose();
                }
            }
        });

        return (port, serving, listener);
    }

    /// <summary>Holds the connection open, saying nothing — the client-speaks-first shape.</summary>
    private static async Task StayQuiet(SslStream tls)
    {
        var buffer = new byte[1];
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tls.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reads one Kafka request and replies with a minimal, correctly-framed ApiVersions response:
    /// length prefix, echoed correlation id, <paramref name="errorCode"/>, empty api-key array.
    /// </summary>
    private static async Task ServeKafkaApiVersions(SslStream tls, short errorCode = 0)
    {
        var header = new byte[4];
        await tls.ReadExactlyAsync(header).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        var request = new byte[size];
        await tls.ReadExactlyAsync(request).ConfigureAwait(false);
        var correlationId = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4, 4));

        var body = new byte[4 + 2 + 4];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), correlationId);
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(4, 2), errorCode);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(6, 4), 0);

        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(0, 4), body.Length);
        body.CopyTo(framed.AsSpan(4));

        await tls.WriteAsync(framed).ConfigureAwait(false);
        await tls.FlushAsync().ConfigureAwait(false);
    }

    private static Task<IReadOnlyList<SecurityConfirmation>> ProbeAsync(
        EnvironmentSpec environment,
        IReadOnlyDictionary<string, object> staged,
        ISecurityConfigurationAccessor accessor,
        params string[] kafkaSpeakingTargets) =>
        SecuredEndpointProbe.ConfirmAsync(
            environment,
            staged,
            accessor,
            new HashSet<string>(kafkaSpeakingTargets, StringComparer.Ordinal),
            ProbeTimeout,
            CancellationToken.None);

    // ── A suite declaring nothing pays nothing ────────────────────────────────────────────

    /// <summary>
    /// A suite with no <c>security</c> block anywhere confirms nothing and contacts nothing — the
    /// probe is entirely off the path of an ordinary run.
    /// </summary>
    [Fact]
    public async Task Confirm_NoSecurityDeclared_ProbesNothing()
    {
        var environment = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["sut"] = new ServiceSpec("acme/sut:1", null, null, null, null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var confirmations = await ProbeAsync(
            environment,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecurityConfigurationAccessor.Instance);

        Assert.Empty(confirmations);
    }

    // ── EDGE-004: the declared endpoint is a plaintext listener ───────────────────────────

    /// <summary>
    /// EDGE-004's core acceptance: a declared endpoint that resolves to a PLAINTEXT listener
    /// aborts the run at the probe with <see cref="OrchestrationErrorKind.SecurityConfirmation"/>,
    /// naming the target and the address. A plaintext port cannot fake a handshake.
    /// </summary>
    [Fact]
    public async Task Confirm_PlaintextListenerOnTheDeclaredEndpoint_FailsAsSecurityConfirmation()
    {
        var (port, serving, listener) = Listen(async client =>
        {
            // Read whatever arrives and answer with plaintext bytes — a listener that speaks a
            // protocol, just not TLS.
            var buffer = new byte[64];
            await client.GetStream().ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("PLAINTEXT")).ConfigureAwait(false);
        });

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                ServiceEnv(Profile("tls")),
                Staged("sut", "127.0.0.1", port),
                new FakeAccessor().With("sut", "tls", null)));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Equal("sut", ex.Info.ResourceName);
            Assert.Contains("TLS handshake", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains($"127.0.0.1:{port}", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── EDGE-005: the broker came up with no SSL listener at all ──────────────────────────

    /// <summary>
    /// EDGE-005: nothing is listening on the declared endpoint — the shape a broker produces when
    /// its entrypoint found no keystore and started PLAINTEXT-only, while the container itself
    /// reports healthy and no ordinary infrastructure signal ever failed.
    /// </summary>
    [Fact]
    public async Task Confirm_NothingListeningOnTheDeclaredEndpoint_FailsAsSecurityConfirmation()
    {
        // Bind then release, so the port is known to be free rather than guessed.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("tls")),
            Staged("sut", "127.0.0.1", closedPort),
            new FakeAccessor().With("sut", "tls", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains("could not connect", ex.Info.Detail, StringComparison.Ordinal);
    }

    // ── A service: transport confirmed, and the limit stated ──────────────────────────────

    /// <summary>
    /// A TLS listener behind a SERVICE target yields
    /// <see cref="SecurityConfirmationLevel.TransportConfirmed"/> — not an authenticated round
    /// trip. This is the honest half of REQ-005: the declaration carries no protocol, so no
    /// application-layer exchange is available, and a completed TLS 1.3 handshake proves the
    /// server's identity and the transport while saying nothing about whether the client's
    /// certificate was accepted.
    /// </summary>
    [Fact]
    public async Task Confirm_TlsServiceTarget_ConfirmsTransportOnlyAndSaysSo()
    {
        var (port, serving, listener) = ListenTls(StayQuiet);

        try
        {
            var confirmations = await ProbeAsync(
                ServiceEnv(Profile("tls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With("sut", "tls", new FakeMaterial(_bed, withClientIdentity: false, declareCa: true)));

            var confirmation = Assert.Single(confirmations);
            Assert.Equal(SecurityConfirmationLevel.TransportConfirmed, confirmation.Level);
            Assert.Equal("service", confirmation.TargetKind);
            Assert.Equal("tls", confirmation.DeclaredProfile);
            Assert.Equal("9093", confirmation.DeclaredEndpoint);
            Assert.Equal($"localhost:{port}", confirmation.ObservedAddress);
            Assert.False(confirmation.ClientIdentityResolved);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// The mutual-TLS service case: the declared client certificate reaches the server, and the
    /// confirmation records that an identity was RESOLVED — while still reporting only
    /// <see cref="SecurityConfirmationLevel.TransportConfirmed"/> and saying in words that
    /// acceptance is confirmed at first step execution.
    /// </summary>
    /// <remarks>
    /// The server's own view is asserted here because it is the only place in this file where
    /// "the certificate really did travel" is checked at all — and note precisely what it does NOT
    /// license: the PROBE cannot see this, which is why
    /// <see cref="SecurityConfirmation.ClientIdentityResolved"/> is named for the declaration it
    /// measures rather than for the wire it does not.
    /// </remarks>
    [Fact]
    public async Task Confirm_MtlsServiceTarget_ResolvesTheClientIdentityButClaimsOnlyTransport()
    {
        X509Certificate2? seenByServer = null;
        var (port, serving, listener) = ListenTls(
            async tls =>
            {
                seenByServer = tls.RemoteCertificate is null ? null : new X509Certificate2(tls.RemoteCertificate);
                await StayQuiet(tls).ConfigureAwait(false);
            },
            ClientAuth.Required);

        try
        {
            var confirmations = await ProbeAsync(
                ServiceEnv(Profile("mtls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With("sut", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true)));

            var confirmation = Assert.Single(confirmations);
            Assert.Equal(SecurityConfirmationLevel.TransportConfirmed, confirmation.Level);
            Assert.True(confirmation.ClientIdentityResolved);
            Assert.Contains("confirmed at first step execution", confirmation.Detail, StringComparison.Ordinal);

            Assert.NotNull(seenByServer);
            Assert.Contains(
                TestCertificateAuthority.ClientSubjectCommonName,
                seenByServer!.Subject,
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
            seenByServer?.Dispose();
        }
    }

    // ── A kafka target: the application-layer round trip ──────────────────────────────────

    /// <summary>
    /// The strong half of REQ-005, in full: a <c>kafka</c> target completes a Kafka ApiVersions
    /// exchange with the declared client certificate AND is refused the same exchange without one,
    /// so the level is <see cref="SecurityConfirmationLevel.AuthenticatedRoundTrip"/>.
    /// </summary>
    /// <remarks>
    /// This is also the DIFFERENTIAL's positive control: it is what rules out the trivial
    /// explanation for
    /// <see cref="Confirm_KafkaTargetAcceptingAnAnonymousClient_FailsAsSecurityConfirmation"/>'s
    /// negative — that the second connection fails for some reason unrelated to the certificate.
    /// The identical listener, on the identical port, answers the identical request when the
    /// declared identity is presented.
    /// </remarks>
    [Fact]
    public async Task Confirm_KafkaTargetRequiringAClientCertificate_ConfirmsAnAuthenticatedRoundTrip()
    {
        var (port, serving, listener) = ListenTls(tls => ServeKafkaApiVersions(tls), ClientAuth.Required);

        try
        {
            var confirmations = await ProbeAsync(
                KafkaDependencyEnv(Profile("mtls")),
                Staged("events", "localhost", port),
                new FakeAccessor().With(
                    "events", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true)));

            var confirmation = Assert.Single(confirmations);
            Assert.Equal(SecurityConfirmationLevel.AuthenticatedRoundTrip, confirmation.Level);
            Assert.Equal("kafka", confirmation.TargetKind);
            Assert.True(confirmation.ClientIdentityResolved);
            Assert.Contains("REFUSED the same request", confirmation.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// <strong>The differential.</strong> A broker that answers the mutual-TLS round trip perfectly
    /// — valid certificate, chain satisfied, <c>errorCode=0</c> — but whose listener does not
    /// REQUIRE a client certificate must fail closed, because it would accept an anonymous client
    /// just as happily.
    /// </summary>
    /// <remarks>
    /// This is one unset <c>KAFKA_SSL_CLIENT_AUTH</c> away from a real deployment: Kafka's
    /// <c>ssl.client.auth</c> defaults to <c>none</c>, and <c>requested</c> (modelled here) behaves
    /// the same way. Without this arm the probe printed the sentence "so it accepted the presented
    /// client certificate" against a peer that never asked for one — a completed round trip proves
    /// the peer did not object, and a peer that never asked objects to nothing.
    /// </remarks>
    [Fact]
    public async Task Confirm_KafkaTargetAcceptingAnAnonymousClient_FailsAsSecurityConfirmation()
    {
        var (port, serving, listener) = ListenTls(tls => ServeKafkaApiVersions(tls), ClientAuth.Requested);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                KafkaDependencyEnv(Profile("mtls")),
                Staged("events", "localhost", port),
                new FakeAccessor().With(
                    "events", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true))));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Equal("events", ex.Info.ResourceName);
            Assert.Contains(
                "presenting NO client certificate", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("ssl.client.auth", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// The differential is a MUTUAL-TLS control and runs only for <c>profile: mtls</c>. Under
    /// <c>profile: tls</c> there is no client identity whose acceptance could be claimed, so a
    /// permissive listener is not a fault — and the confirmation says so instead of claiming an
    /// acceptance that never happened.
    /// </summary>
    [Fact]
    public async Task Confirm_KafkaTargetUnderTlsProfile_SkipsTheDifferentialAndClaimsNoIdentity()
    {
        var (port, serving, listener) = ListenTls(tls => ServeKafkaApiVersions(tls), ClientAuth.Requested);

        try
        {
            var confirmations = await ProbeAsync(
                KafkaDependencyEnv(Profile("tls")),
                Staged("events", "localhost", port),
                new FakeAccessor().With(
                    "events", "tls", new FakeMaterial(_bed, withClientIdentity: false, declareCa: true)));

            var confirmation = Assert.Single(confirmations);
            Assert.Equal(SecurityConfirmationLevel.AuthenticatedRoundTrip, confirmation.Level);
            Assert.False(confirmation.ClientIdentityResolved);
            Assert.Contains("none was accepted and none is claimed", confirmation.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// m1: a correctly-framed reply carrying a non-zero <c>error_code</c> is a REFUSAL, not a round
    /// trip. Reading only the correlation id scored <c>error_code = 58</c>
    /// (<c>SASL_AUTHENTICATION_FAILED</c>) as a successful authenticated exchange.
    /// </summary>
    [Fact]
    public async Task Confirm_KafkaTargetAnsweringWithANonZeroErrorCode_Fails()
    {
        var (port, serving, listener) = ListenTls(tls => ServeKafkaApiVersions(tls, errorCode: 58), ClientAuth.Required);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                KafkaDependencyEnv(Profile("mtls")),
                Staged("events", "localhost", port),
                new FakeAccessor().With(
                    "events", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true))));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Contains("error_code 58", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── DECISION-1: the level follows the protocol the STEPS will speak ───────────────────

    /// <summary>
    /// REQ-011's shape: the customer's mTLS broker is authored as a SERVICE, not as the
    /// engine-provisioned <c>kafka</c> dependency type. A <c>mq-publish.kafka</c> step naming that
    /// service is what tells the probe the protocol, so it earns the same authenticated round trip
    /// a <c>kafka</c> dependency does — rather than the transport-only ceiling.
    /// </summary>
    [Fact]
    public async Task Confirm_ServiceTargetedByAKafkaStep_EarnsTheAuthenticatedRoundTrip()
    {
        var (port, serving, listener) = ListenTls(tls => ServeKafkaApiVersions(tls), ClientAuth.Required);

        try
        {
            var confirmations = await ProbeAsync(
                ServiceEnv(Profile("mtls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With("sut", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true)),
                "sut");

            var confirmation = Assert.Single(confirmations);
            Assert.Equal(SecurityConfirmationLevel.AuthenticatedRoundTrip, confirmation.Level);
            Assert.Equal("service", confirmation.TargetKind);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// The other half of DECISION-1, and the reason it is not a guess: a service NO Kafka step
    /// targets stays on the transport-only branch. Nothing sniffs, and no Kafka framing is written
    /// into a connection that might be HTTP.
    /// </summary>
    [Fact]
    public async Task Confirm_ServiceNotTargetedByAKafkaStep_StaysTransportOnly()
    {
        var (port, serving, listener) = ListenTls(StayQuiet, ClientAuth.Required);

        try
        {
            var confirmations = await ProbeAsync(
                ServiceEnv(Profile("mtls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With("sut", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true)),
                "some-other-broker");

            var confirmation = Assert.Single(confirmations);
            Assert.Equal(SecurityConfirmationLevel.TransportConfirmed, confirmation.Level);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── MAJOR-2: a 0-byte read is EOF, not silence ────────────────────────────────────────

    /// <summary>
    /// The commonest rejection shape of all: the peer completes the handshake and then CLOSES.
    /// The bounded read returns 0 bytes — end-of-stream — and discarding that count scored it as
    /// "no rejection raised". The nginx arm that motivated the rejection detector happens to time
    /// out; HAProxy, Envoy and a Java server with <c>needClientAuth</c> all close instead.
    /// </summary>
    [Fact]
    public async Task Confirm_PeerClosingImmediatelyAfterTheHandshake_IsReportedAsARejection()
    {
        var (port, serving, listener) = ListenTls(
            async tls =>
            {
                await tls.ShutdownAsync().ConfigureAwait(false);
            },
            ClientAuth.Required);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                ServiceEnv(Profile("mtls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With(
                    "sut", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true))));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Contains("the peer then rejected the connection", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("0-byte read is end-of-stream", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// The measurement that makes the round trip worth having: a TLS endpoint that completes the
    /// handshake and then does NOT answer the Kafka request fails, even though the handshake
    /// itself succeeded. This is the shape a broker produces when it rejects the client
    /// certificate — under TLS 1.3 that refusal arrives AFTER the handshake — and also the shape a
    /// TLS endpoint that simply is not a Kafka broker produces.
    /// </summary>
    [Fact]
    public async Task Confirm_KafkaTargetThatCompletesTheHandshakeButDoesNotSpeakKafka_Fails()
    {
        // Handshake, then close without answering — indistinguishable, at the TLS layer, from a
        // successful handshake that will be honoured. Only the round trip separates them.
        var (port, serving, listener) = ListenTls(_ => Task.CompletedTask, ClientAuth.Required);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                KafkaDependencyEnv(Profile("mtls")),
                Staged("events", "localhost", port),
                new FakeAccessor().With(
                    "events", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true))));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Contains("did not answer a Kafka ApiVersions request", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains(
                "does not prove the client certificate was accepted", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── m6: every exit from the probe carries SecurityConfirmation ────────────────────────

    /// <summary>
    /// The outer token cancelling DURING the post-handshake read must fail closed as a
    /// security-confirmation failure, not propagate raw.
    /// </summary>
    /// <remarks>
    /// The bounded read's own grace window is a normal, quiet outcome; the CALLER's token
    /// cancelling is not, and collapsing the two lost REQ-018's signal entirely —
    /// <c>ScenarioRunner</c> sets <c>SecurityConfirmationFailed</c> only inside
    /// <c>catch (OrchestrationException)</c>, so a raw <see cref="OperationCanceledException"/>
    /// escaped past it. Nothing was confirmed, so nothing is claimed.
    /// </remarks>
    [Fact]
    public async Task Confirm_OuterTokenCancelledDuringThePostHandshakeRead_FailsAsSecurityConfirmation()
    {
        using var cts = new CancellationTokenSource();

        // Cancellation is triggered from the SERVER, after its own handshake has completed, rather
        // than on a wall-clock timer: a timer raced the handshake and landed in
        // AuthenticateAsClientAsync often enough to test a different branch than the one intended.
        // The short delay puts it inside the client's 1 s grace read, which is where the outcome
        // under test is decided.
        var (port, serving, listener) = ListenTls(async tls =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            await StayQuiet(tls).ConfigureAwait(false);
        });

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SecuredEndpointProbe.ConfirmAsync(
                    ServiceEnv(Profile("tls")),
                    Staged("sut", "localhost", port),
                    new FakeAccessor().With(
                        "sut", "tls", new FakeMaterial(_bed, withClientIdentity: false, declareCa: true)),
                    new HashSet<string>(StringComparer.Ordinal),
                    ProbeTimeout,
                    cts.Token));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Contains("Nothing was confirmed", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── Fail-closed on the declaration itself ─────────────────────────────────────────────

    /// <summary>
    /// EDGE-010's spirit at the declaration level: <c>profile: mtls</c> with no client identity
    /// resolved is refused before any handshake. Continuing would connect with no client identity
    /// at all, and against a listener that requests but does not require one that is a green suite
    /// which authenticated nothing.
    /// </summary>
    [Fact]
    public async Task Confirm_MtlsWithNoResolvedClientIdentity_FailsBeforeConnecting()
    {
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("mtls")),
            Staged("sut", "127.0.0.1", 1),
            new FakeAccessor().With("sut", "mtls", new FakeMaterial(_bed, withClientIdentity: false, declareCa: true))));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains("no 'clientCert'/'clientKey' pair resolved", ex.Info.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A declared target the topology staged no address for cannot be confirmed, and that is a
    /// failure rather than a skip: silently confirming nothing is the whole failure mode this
    /// requirement exists to close.
    /// </summary>
    [Fact]
    public async Task Confirm_TargetWithNoStagedAddress_Fails()
    {
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("tls")),
            new Dictionary<string, object>(StringComparer.Ordinal),
            new FakeAccessor().With("sut", "tls", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains("staged no reachable address", ex.Info.Detail, StringComparison.Ordinal);
    }

    // ── The declared trust anchor is consulted, and is load-bearing ───────────────────────

    /// <summary>
    /// A declared <c>caCert</c> makes the target's own
    /// <see cref="ISecurityCertificateMaterial.TrustsRemoteCertificate"/> the verdict: when it
    /// refuses, the handshake fails and the probe reports a security-confirmation failure. This
    /// pins that the probe judges the peer by the SAME rule a step will — a probe using a
    /// different rule could pass a topology the step then rejects, or pass one it should not have.
    /// </summary>
    [Fact]
    public async Task Confirm_DeclaredAnchorRefusingThePeer_Fails()
    {
        var (port, serving, listener) = ListenTls(StayQuiet);
        var material = new FakeMaterial(_bed, withClientIdentity: false, declareCa: true, trusts: false);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                ServiceEnv(Profile("tls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With("sut", "tls", material)));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.True(material.TrustDecisions > 0, "the declared anchor was never consulted");
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// With NO <c>caCert</c> declared the engine installs no callback at all and the PLATFORM's
    /// own verdict stands — which, for this bed's private CA, means the handshake is refused. The
    /// engine neither narrows nor relaxes, exactly as REQ-024 fixes for the HTTP path.
    /// </summary>
    [Fact]
    public async Task Confirm_NoDeclaredAnchorAgainstAPrivateCa_FailsOnThePlatformsOwnVerdict()
    {
        var (port, serving, listener) = ListenTls(StayQuiet);
        var material = new FakeMaterial(_bed, withClientIdentity: true, declareCa: false);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                ServiceEnv(Profile("mtls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With("sut", "mtls", material)));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);

            // No callback was installed, so the material's own trust decision was never asked for.
            Assert.Equal(0, material.TrustDecisions);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── Reporting shape ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// REQ-005's reporting requirement: the rendered line names what was DECLARED and what was
    /// OBSERVED, not merely that something passed.
    /// </summary>
    [Fact]
    public async Task Confirm_RenderedLine_NamesDeclaredAndObserved()
    {
        var (port, serving, listener) = ListenTls(StayQuiet);

        try
        {
            var confirmations = await ProbeAsync(
                ServiceEnv(Profile("tls")),
                Staged("sut", "localhost", port),
                new FakeAccessor().With("sut", "tls", new FakeMaterial(_bed, withClientIdentity: false, declareCa: true)));

            var line = Assert.Single(confirmations).ToString();

            Assert.Contains("declared profile 'tls'", line, StringComparison.Ordinal);
            Assert.Contains("endpoint '9093'", line, StringComparison.Ordinal);
            Assert.Contains($"observed Tls", line, StringComparison.Ordinal);
            Assert.Contains($"localhost:{port}", line, StringComparison.Ordinal);

            // m2: the rendered line reports what was measured — an identity was RESOLVED from the
            // declaration — and never asserts presentation, which this side cannot observe.
            Assert.Contains("client identity none declared", line, StringComparison.Ordinal);
            Assert.DoesNotContain("presented", line, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── MAJOR-1 (fix round three): the differential's asymmetry stops at the connect ───────

    /// <summary>
    /// <strong>A connect-phase failure is not a certificate refusal.</strong> A listener that
    /// serves the FIRST connection perfectly — TLS, declared anchor satisfied, a clean
    /// <c>ApiVersions</c> round trip with the declared client certificate — and then refuses the
    /// SECOND at the TCP layer must fail closed as UNCONFIRMED, never as "the broker requires an
    /// identity".
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the file-header defect turned outward. The header already records that a one-shot
    /// listener would make the differential "pass" for the wrong reason, and every other listener
    /// here serves in a loop to avoid it — but the mechanism was live in the PRODUCTION probe,
    /// where the connect sat inside the try whose catch read any failure as a refusal. A
    /// connection refused, a reset, a full backlog, an exhausted ephemeral-port range or a
    /// container restarting between the two arms all happen before any TLS and say nothing about
    /// <c>ssl.client.auth</c>.
    /// </para>
    /// <para>
    /// MEASURED RED against the pre-fix source, not argued: with the second <c>ConnectAsync</c>
    /// moved back inside the shared try and <c>SocketException</c> restored to the evidence
    /// filter, this exact listener made the probe throw NOTHING and return
    /// <c>Level = AuthenticatedRoundTrip</c> carrying, verbatim, "the broker answered a Kafka
    /// ApiVersions request over this connection, and REFUSED the same request on a second
    /// connection presenting no client certificate — so it both accepted the declared client
    /// identity and requires one" — from a peer that had simply stopped listening.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Confirm_SecondConnectionRefusedAtTheTcpLayer_FailsAsUnconfirmedNotAsRefusal()
    {
        var (port, serving, listener) = ListenTlsOnce(
            tls => ServeKafkaApiVersions(tls), ClientAuth.Required);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
                KafkaDependencyEnv(Profile("mtls")),
                Staged("events", "localhost", port),
                new FakeAccessor().With(
                    "events", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true))));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Equal("events", ex.Info.ResourceName);
            Assert.Contains(
                "could not open a second connection", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains(
                "before any TLS", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("nothing is claimed", ex.Info.Detail, StringComparison.Ordinal);

            // The claim the pre-fix implementation reached from this same listener.
            Assert.DoesNotContain("REFUSED", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── m4: the two cancellation branches, previously correct and untested ────────────────

    /// <summary>
    /// The outer token cancelling DURING the Kafka round trip on the FIRST connection fails closed
    /// as a security-confirmation failure — the branch that keeps a cancelled probe from being
    /// read as a confirmed one.
    /// </summary>
    [Fact]
    public async Task Confirm_OuterTokenCancelledDuringTheKafkaRoundTrip_FailsAsSecurityConfirmation()
    {
        using var cts = new CancellationTokenSource();

        // Cancellation is triggered from the SERVER once its own handshake has completed — the
        // same pattern the post-handshake-read cancellation test uses, and for the same reason: a
        // wall-clock timer lands in AuthenticateAsClientAsync often enough to test a different
        // branch than the one intended. The server then never answers, so the client's
        // ApiVersions read is what observes the cancellation.
        var (port, serving, listener) = ListenTls(
            async tls =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                await cts.CancelAsync().ConfigureAwait(false);
                await StayQuiet(tls).ConfigureAwait(false);
            },
            ClientAuth.Required);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SecuredEndpointProbe.ConfirmAsync(
                    KafkaDependencyEnv(Profile("mtls")),
                    Staged("events", "localhost", port),
                    new FakeAccessor().With(
                        "events", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true)),
                    new HashSet<string>(StringComparer.Ordinal),
                    ProbeTimeout,
                    cts.Token));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Contains(
                "did not answer a Kafka ApiVersions request", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    /// <summary>
    /// The outer token cancelling DURING the anonymous-client differential reports an UNFINISHED
    /// check rather than a refusal — the distinction the differential exists to hold, since a
    /// probe that ran out of time confirmed nothing.
    /// </summary>
    [Fact]
    public async Task Confirm_OuterTokenCancelledDuringTheDifferential_FailsAsUnfinishedNotAsRefusal()
    {
        using var cts = new CancellationTokenSource();
        var connections = 0;

        // Connection 1 answers the round trip normally; connection 2 — the anonymous arm — waits,
        // cancels the caller's token and then stays silent, so the differential is cancelled
        // rather than refused. The listener accepts a null client certificate deliberately: if it
        // refused one, the arm would take its refusal path and never reach the cancellation.
        var (port, serving, listener) = ListenTls(
            async tls =>
            {
                if (Interlocked.Increment(ref connections) == 1)
                {
                    await ServeKafkaApiVersions(tls).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                await cts.CancelAsync().ConfigureAwait(false);
                await StayQuiet(tls).ConfigureAwait(false);
            },
            ClientAuth.Requested);

        try
        {
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SecuredEndpointProbe.ConfirmAsync(
                    KafkaDependencyEnv(Profile("mtls")),
                    Staged("events", "localhost", port),
                    new FakeAccessor().With(
                        "events", "mtls", new FakeMaterial(_bed, withClientIdentity: true, declareCa: true)),
                    new HashSet<string>(StringComparer.Ordinal),
                    ProbeTimeout,
                    cts.Token));

            Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
            Assert.Contains(
                "could not finish confirming", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains(
                "an unfinished one is reported rather than assumed",
                ex.Info.Detail,
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    // ── m3 (security review, fix round four): the unknown-profile branch is ARMED ─────────
    //
    // SecuredEndpointProbe.ProfilesPresentingAClientIdentity is an exhaustive map — profile → does
    // it present a client identity — precisely so "absent" and "present but false" stay distinct.
    // The call site used to fold them together with `TryGetValue(…) && presents`, so an
    // unrecognised profile silently took the no-client-identity path, skipped the anonymous-client
    // differential, and produced a transport-only confirmation. SecurityProfileVocabularyDriftTests
    // (Vouchfx.Engine.Runtime.Tests, which sees both assemblies' internals) makes that drift a red
    // build at the moment of registration; this is the run-time backstop under it.

    /// <summary>
    /// A <c>security.profile</c> the probe has no entry for fails the confirmation CLOSED, naming
    /// the profile — it never degrades to the transport-only path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unreachable from a suite on <c>vouchfx run</c> (the root schema narrows <c>profile</c> to the
    /// registered set, REQ-021, and the wiring validator rejects an unresolved pair pre-topology,
    /// REQ-022 — both inside <c>ProviderPipeline.Compile</c>), so it is driven through the probe's
    /// own internal entry point. It IS author-reachable under <c>--watch</c>, whose compile seam runs
    /// neither of those (MAJOR-2, fix round five), which is why the message asserted below is
    /// written for an author rather than for an engine maintainer.
    /// </para>
    /// <para>
    /// The staged address points at a port nothing serves and the target is one with a resolvable
    /// address: the guard must fire BEFORE any socket work, so if it did not, this would fail with
    /// the connect diagnostic instead — which is what the penultimate assertion pins.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Confirm_ProfileTheProbeDoesNotRecognise_FailsClosedNamingTheProfile()
    {
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("kerberos")),
            Staged("sut", "127.0.0.1", 1),
            new FakeAccessor().With("sut", "kerberos", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Equal("sut", ex.Info.ResourceName);
        Assert.Contains("'kerberos'", ex.Info.Detail, StringComparison.Ordinal);
        Assert.Contains("this engine recognises", ex.Info.Detail, StringComparison.Ordinal);

        // AUTHOR-FACING: it names the vocabulary the author can choose from and tells them what a
        // typo costs. Naming the set is the half a maintainer-only message left out.
        Assert.Contains("Supported profiles: mtls, tls", ex.Info.Detail, StringComparison.Ordinal);
        Assert.Contains("typo", ex.Info.Detail, StringComparison.Ordinal);

        // The refusal happened before the socket, not after it.
        Assert.DoesNotContain("could not connect", ex.Info.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same refusal, on a target the topology staged NO address for: the profile is still what
    /// gets named (m7, security review, fix round five).
    /// </summary>
    /// <remarks>
    /// Both orderings fail closed, so this pins legibility rather than safety. With the guard sited
    /// after <c>TryResolveAddress</c>, this suite reported "staged no reachable address" — true, but
    /// the profile is the fact the author can act on, and it was never mentioned.
    /// </remarks>
    [Fact]
    public async Task Confirm_UnrecognisedProfileOnATargetWithNoStagedAddress_NamesTheProfile()
    {
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("kerberos")),
            new Dictionary<string, object>(StringComparer.Ordinal),
            new FakeAccessor().With("sut", "kerberos", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains("'kerberos'", ex.Info.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("staged no reachable address", ex.Info.Detail, StringComparison.Ordinal);
    }

    // ── nit (security review, fix round four): IPv6 authority parsing ────────────────────
    //
    // TryResolveAddress used to split a bare authority on LastIndexOf(':'). MEASURED on .NET 8:
    // '[::1]:9093' kept its brackets, so TcpClient.ConnectAsync resolved the literal text '[::1]'
    // as a DNS NAME; and a bracketless 'fe80::1' with no port split into host 'fe80:' port 1 — a
    // plausible-looking address that is not the declared target. Both failed CLOSED, so this is
    // correctness of the MESSAGE rather than of the verdict, and nothing in this release stages an
    // IPv6 authority. Both are asserted through the diagnostic, which is where the parse is
    // observable: the probe's private resolver has no other seam.

    /// <summary>
    /// A bracketed IPv6 authority resolves to the bracket-free address — the form
    /// <c>ConnectAsync</c> can actually use — and the diagnostic re-brackets it, so what a reader
    /// sees is a parseable authority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The port is one just released by a listener, so nothing serves it and the probe necessarily
    /// reports a failure naming the address it resolved. Robust to a host without IPv6: an
    /// unavailable <c>::1</c> fails at the same connect and produces the same address in the same
    /// message.
    /// </para>
    /// <para>
    /// <strong>The assertion flipped in fix round five (m1).</strong> It used to assert
    /// <c>DoesNotContain("[::1]")</c> — which pinned the CONNECT form, but in doing so pinned the
    /// diagnostic to <c>::1:9093</c>: not a parseable authority, and ambiguous about where the
    /// address ends. The resolver still yields the bracket-free host;
    /// <c>AuthorityText.Format</c> re-brackets it for display only.
    /// </para>
    /// <para>
    /// <strong>The double-bracket assertion below no longer discriminates a bracket-retaining
    /// parse</strong> (m5, fix round seven — recorded here rather than left for a reader to
    /// discover). It used to: a parser that retained the brackets would have handed
    /// <c>AuthorityText.Format</c> the host <c>[::1]</c>, which contains a colon and was therefore
    /// bracketed AGAIN, rendering <c>[[::1]]</c>. That round made <c>Format</c> idempotent on an
    /// already-bracketed host, so such a parse would now render <c>[::1]:9093</c> here and satisfy
    /// both assertions. <c>AuthorityTextTests</c> pins the formatter's own rule directly, but the
    /// probe's private resolver has no seam other than this message (see the note above this test),
    /// so nothing currently pins IT to the bracket-free form. The assertion is kept as a cheap
    /// rendering guard; restoring the parse coverage would mean giving that resolver a test seam.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Confirm_BracketedIpv6Authority_ResolvesWithoutTheBracketsAndReportsWithThem()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("tls")),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["sut"] = $"[::1]:{freePort}" },
            new FakeAccessor().With("sut", "tls", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains($"[::1]:{freePort}", ex.Info.Detail, StringComparison.Ordinal);

        // A rendering guard, no longer a parse discriminator — see the remarks above (m5).
        Assert.DoesNotContain("[[::1]]", ex.Info.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A staged value carrying an <c>@</c> is REFUSED rather than reinterpreted (m2 / NIT-1,
    /// security review, fix round five).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row that matters is the second. MEASURED on the pinned runtime:
    /// <c>Uri.TryCreate("tcp://@host:9093", …)</c> succeeds with an EMPTY <c>UserInfo</c>, host
    /// <c>host</c> and port 9093 — so the <c>UserInfo.Length != 0</c> guard let it through and the
    /// probe would have connected to a REACHABLE host that is not the text staged. It is the one
    /// input in security's 74-value corpus that retargets rather than merely failing differently.
    /// </para>
    /// <para>
    /// Neither row is author-reachable today; the guard is on the delimiter anyway because this
    /// parser decides what address a security probe connects to.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("user@host.invalid:9093")]
    [InlineData("@host.invalid:9093")]
    public async Task Confirm_BareAuthorityCarryingUserInfo_IsRefusedAsUnresolvable(string staged)
    {
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("tls")),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["sut"] = staged },
            new FakeAccessor().With("sut", "tls", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains("staged no reachable address", ex.Info.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bracketless IPv6 literal carrying no port is REFUSED as unresolvable rather than split
    /// into a fabricated host and port.
    /// </summary>
    /// <remarks>
    /// The old split turned <c>fe80::1</c> into host <c>fe80:</c> / port <c>1</c> and reported a
    /// connect failure against it — blaming the connection for a parse. No socket is opened on this
    /// path at all, so the test is entirely deterministic.
    /// </remarks>
    [Theory]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    public async Task Confirm_BracketlessIpv6LiteralWithNoPort_IsRefusedAsUnresolvable(string staged)
    {
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("tls")),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["sut"] = staged },
            new FakeAccessor().With("sut", "tls", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains("staged no reachable address", ex.Info.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shapes every fabric in this release actually stages still resolve — the regression guard
    /// on the parser swap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted through the connect diagnostic, which names the resolved <c>host:port</c> verbatim:
    /// a value that failed to resolve would produce "staged no reachable address" instead, and a
    /// value that resolved WRONGLY would name something other than what went in.
    /// </para>
    /// <para>
    /// The third row is the one that discriminates. A bare DNS name is a legal URI SCHEME, so
    /// <c>Uri.TryCreate("broker.invalid:9093", UriKind.Absolute, …)</c> SUCCEEDS with that text as
    /// the scheme and an empty host — measured — which is why the URL branch must require a non-empty
    /// host and a real port before it claims the value, and why this row must reach the bare-authority
    /// branch to pass. <c>.invalid</c> is RFC 6761-reserved and guaranteed never to resolve, so the
    /// row cannot depend on the resolver it runs against (measured: 5 ms here, against 2.7 s for a
    /// bare <c>broker</c> that reaches the network before failing).
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    [InlineData("broker.invalid", "broker.invalid")]
    public async Task Confirm_OrdinaryBareAuthority_ResolvesUnchanged(string stagedHost, string expectedHost)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => ProbeAsync(
            ServiceEnv(Profile("tls")),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["sut"] = $"{stagedHost}:{freePort}" },
            new FakeAccessor().With("sut", "tls", null)));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains($"{expectedHost}:{freePort}", ex.Info.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("staged no reachable address", ex.Info.Detail, StringComparison.Ordinal);
    }
}
