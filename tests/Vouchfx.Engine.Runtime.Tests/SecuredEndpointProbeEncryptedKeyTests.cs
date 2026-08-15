// EDGE-005 — an encrypted client key satisfies SecuredEndpointProbe unchanged
// (client-key-password).
//
// WHY THIS FILE IS HERE AND NOT BESIDE THE OTHER PROBE TESTS. EDGE-005 needs the probe and the
// REAL SecurityConfigurationAccessor in one process, and no existing test project can host both:
//
//   • SecuredEndpointProbe is internal to Vouchfx.Engine.Orchestration, whose InternalsVisibleTo
//     names Vouchfx.Engine.Orchestration.Tests AND Vouchfx.Engine.Runtime.Tests — so the probe is
//     reachable from either.
//   • SecurityConfigurationAccessor is internal to Vouchfx.Engine.Runtime, whose
//     InternalsVisibleTo names Vouchfx.Engine.Runtime.Tests, `vouchfx` and the memory harness —
//     and NOT Vouchfx.Engine.Orchestration.Tests.
//
// So tests/Vouchfx.Engine.Orchestration.Tests/SecuredEndpointProbeTests.cs, which owns every other
// probe arm, can only ever hand the probe a hand-built ISecurityCertificateMaterial — a fake whose
// own file loading proves nothing about the production one. This assembly is the only place the
// two halves meet, and putting the arm here costs a local listener rather than a widened
// InternalsVisibleTo on a production project. The same assembly-boundary argument, in the same
// words, is why WatchProbeSecurityWiringTests exists in this project rather than in the CLI's.
//
// WHAT THE ARM PROVES, precisely. EDGE-005 asserts that an encrypted key with a valid passphrase
// satisfies the probe's client-identity load and its anonymous-client differential UNCHANGED, and
// that the probe gains no second load path of its own. The two halves are evidenced differently:
//
//   • BEHAVIOUR — the test below. The probe reaches AuthenticatedRoundTrip against a listener that
//     REQUIRES a client certificate, which it can only do by (a) loading a usable identity out of
//     an AES-256-CBC-encrypted PEM key using a passphrase resolved from a ${secret:env/…}
//     reference, and (b) being refused the same round trip on its second, anonymous connection.
//     Both halves of the differential run; neither is stubbed.
//   • ABSENCE OF A SECOND PATH — the probe reads `certificates?.ClientCertificate` and nothing
//     else, so there is no second path to exercise. That is a property of SecuredEndpointProbe's
//     source, which has zero diff across this series, and it is stated here rather than asserted:
//     a test cannot prove the non-existence of code.
//
// NOT DECORATIVE — measured, by two mutations run against this arm and then reverted:
//   1. SecurityConfigurationAccessor.LoadClient forced onto its PLAINTEXT branch
//      (`if (passphrase is null || true)`) — RED, and the stack ran through
//      SecuredEndpointProbe.ConfirmOneAsync's `certificates?.ClientCertificate` read, which is the
//      one member EDGE-005 names. The decrypting load is genuinely on this arm's path.
//   2. This file's listener relaxed to `ssl.client.auth=requested` (accept a null client
//      certificate) — RED with the probe's own anonymous-differential diagnostic ("answered the
//      SAME request on a second connection presenting NO client certificate"). The differential
//      genuinely runs here rather than being inferred from the level alone.
//
// Non-Docker. Certificates are generated in-process by TestCertificateAuthority and the listener is
// an ordinary loopback socket, exactly as SecuredEndpointProbeTests establishes for the same probe.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// EDGE-005: the REQ-005 probe confirms a mutual-TLS target whose declared <c>clientKey</c> is an
/// ENCRYPTED PEM unlocked by a <c>clientKeyPassword</c> secret reference, reaching the same
/// <see cref="SecurityConfirmationLevel.AuthenticatedRoundTrip"/> a plaintext key reaches.
/// </summary>
public sealed class SecuredEndpointProbeEncryptedKeyTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// EDGE-005's acceptance. An encrypted client key plus a valid <c>${secret:env/…}</c>
    /// passphrase satisfies the probe's client-identity load AND its anonymous-client
    /// differential: the level reached is <c>AuthenticatedRoundTrip</c>, which the probe awards
    /// only when the identity completed the Kafka round trip and a second, certificate-less
    /// connection did not.
    /// </summary>
    /// <remarks>
    /// The listener REQUIRES a client certificate and refuses a null one at the TLS layer, which is
    /// what <c>ssl.client.auth=required</c> does — the permissive shape would let the probe reach
    /// the same round trip with no identity at all and would make this arm a false green for the
    /// exact control it is meant to exercise.
    /// </remarks>
    [Fact]
    public async Task Confirm_EncryptedClientKeyWithAValidPassphrase_ReachesTheAuthenticatedRoundTrip()
    {
        const string passphrase = "pilot-passphrase";

        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, passphrase);

        // Unique per test and cleared in a finally: this repo has a measured collision hazard when
        // two arms share one variable name under a parallelised runner.
        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, passphrase);

        X509Certificate2? seenByServer = null;
        var (port, serving, listener) = ListenTlsRequiringAClientCertificate(
            bed.ServerCertificate,
            async tls =>
            {
                if (tls.RemoteCertificate is { } raw)
                {
                    seenByServer ??= new X509Certificate2(raw);
                }

                await ServeKafkaApiVersions(tls).ConfigureAwait(false);
            });

        try
        {
            var ledger = new ResolvedSecretLedger();
            using var secrets = ScenarioRunner.CreateSecretAccessorScope(ledger);

            var ast = AstWithSecuredKafkaDependency(
                "events", MtlsSecurityWithPassphrase("${secret:env/" + variable + "}"));

            var accessor = SecurityConfigurationAccessor.Build(ast, bed.SuiteDirectory, secrets.Accessor);
            try
            {
                var confirmations = await SecuredEndpointProbe.ConfirmAsync(
                    ast.Environment,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["events"] = $"localhost:{port}",
                    },
                    accessor,
                    new HashSet<string>(StringComparer.Ordinal) { "events" },
                    ProbeTimeout,
                    CancellationToken.None);

                var confirmation = Assert.Single(confirmations);

                // The STRONG level. Awarded only when both halves ran: the declared identity
                // completed the round trip, and the anonymous second connection did not.
                Assert.Equal(SecurityConfirmationLevel.AuthenticatedRoundTrip, confirmation.Level);
                Assert.Equal("kafka", confirmation.TargetKind);
                Assert.True(confirmation.ClientIdentityResolved);
                Assert.Contains("REFUSED the same request", confirmation.Detail, StringComparison.Ordinal);

                // The identity really travelled, and it is the declared one — the half the probe
                // cannot see for itself (see SecurityConfirmation.ClientIdentityResolved's own
                // remarks on why it is named for the declaration rather than the wire).
                Assert.NotNull(seenByServer);
                Assert.Contains(
                    TestCertificateAuthority.ClientSubjectCommonName,
                    seenByServer!.Subject,
                    StringComparison.Ordinal);

                // The passphrase was USED, not merely accepted. An unused one would leave the
                // ledger empty — and an encrypted key would not have loaded at all.
                Assert.Equal(1, ledger.Count);
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }
        finally
        {
            listener.Stop();
            await serving;
            seenByServer?.Dispose();
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────
    //
    // Deliberately local. SecuredEndpointProbeTests' equivalents are private to that class in
    // another assembly, and lifting them into Vouchfx.TestSupport to share them would move a
    // listener two dozen probe arms depend on for the sake of this one.

    /// <summary>
    /// A loopback TLS listener that serves connections in a LOOP and REQUIRES a client certificate,
    /// refusing a null one at the TLS layer.
    /// </summary>
    /// <remarks>
    /// Serving in a loop is load-bearing, not incidental: for <c>profile: mtls</c> the probe opens a
    /// SECOND connection presenting no client certificate, and a one-shot listener would refuse it
    /// at the TCP layer — the differential would then "pass" for a reason unrelated to the
    /// certificate, which is a false green in the test for the control that exists to prevent false
    /// greens.
    /// </remarks>
    private static (int Port, Task Serving, TcpListener Listener) ListenTlsRequiringAClientCertificate(
        X509Certificate2 serverCertificate, Func<SslStream, Task> afterHandshake)
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
                            var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                            try
                            {
                                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = serverCertificate,
                                    ClientCertificateRequired = true,

                                    // SERVER-side: refuse a null client certificate, which is what
                                    // `ssl.client.auth=required` does. What is under test is the
                                    // PROBE, never this listener's own trust decision, so no chain
                                    // is built here. CA5359 cannot tell a server-side callback from
                                    // a client validating a server; this one never leaves loopback.
#pragma warning disable CA5359
                                    RemoteCertificateValidationCallback =
                                        static (_, certificate, _, _) => certificate is not null,
#pragma warning restore CA5359
                                }).ConfigureAwait(false);

                                await afterHandshake(tls).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // The deliberately-refused anonymous second connection lands here;
                                // it is an expected outcome, not a fault.
                            }
                            finally
                            {
                                tls.Dispose();
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

    /// <summary>
    /// Reads one Kafka request and replies with a minimal, correctly-framed ApiVersions response:
    /// length prefix, echoed correlation id, <c>errorCode=0</c>, empty api-key array.
    /// </summary>
    private static async Task ServeKafkaApiVersions(SslStream tls)
    {
        var header = new byte[4];
        await tls.ReadExactlyAsync(header).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        var request = new byte[size];
        await tls.ReadExactlyAsync(request).ConfigureAwait(false);
        var correlationId = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4, 4));

        var body = new byte[4 + 2 + 4];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), correlationId);
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(6, 4), 0);

        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(0, 4), body.Length);
        body.CopyTo(framed.AsSpan(4));

        await tls.WriteAsync(framed).ConfigureAwait(false);
        await tls.FlushAsync().ConfigureAwait(false);
    }

    private static SecuritySpec MtlsSecurityWithPassphrase(string reference) =>
        new(
            Profile: "mtls",
            Endpoint: "9093",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: TestCertificateAuthority.ClientCertFileName,
            ClientKey: TestCertificateAuthority.ClientKeyFileName,
            ServerArtifacts: null)
        {
            ClientKeyPassword = reference,
        };

    /// <summary>
    /// One AST carrying the declaration BOTH halves read: the accessor projects its certificate
    /// material from it, and the probe walks the same <c>Environment</c> for secured targets. Two
    /// separately-built specs would let the halves disagree about what was declared, which is the
    /// one thing this arm must not permit.
    /// </summary>
    private static ScenarioAst AstWithSecuredKafkaDependency(string name, SecuritySpec security) =>
        new(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: null,
                Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
                {
                    [name] = new DependencySpec("kafka", null, null) { Security = security },
                },
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());
}
