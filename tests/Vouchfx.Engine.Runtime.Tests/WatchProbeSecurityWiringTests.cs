// EDGE-007 — the `--watch` probe's client-identity load (client-key-password, T8).
//
// WHY THIS FILE EXISTS SEPARATELY FROM SecurityConfigurationAccessorTests.
// #364 recorded that `WatchRunner` was a SECOND implementation of the run path with no
// Docker-free test seam, and EDGE-007 answered that by giving the watch path its own coverage
// rather than letting it inherit the run path's. #364's structural fix has since landed and the
// watch seams ARE reachable without Docker — but not with real certificate material, so what this
// file executes is still the only evidence that the composition RESOLVES anything (see the
// `TheStructuralHalfOfTheEvidence_StillExists` remarks below for the exact division). The two arms
// below therefore assemble the
// probe's security accessor from EXACTLY the two production parts WatchRunner's build seam
// composes, in the same order:
//
//     var probeSecrets = ScenarioRunner.CreateSecretAccessorScope(<session ledger>);
//     probeSecurity    = SecurityConfigurationAccessor.Build(
//                            ast, suiteDirectory, probeSecrets.Accessor, <session path ledger>);
//
// That composition is the thing under test, and it is materially different from what
// SecurityConfigurationAccessorTests exercises: those tests hand `Build` a hand-rolled
// `SecretAccessor` over a hand-picked resolver list, which cannot tell you whether the
// PRODUCTION resolver set — `ScenarioRunner.BuildSecretResolvers`, reached only through
// `CreateSecretAccessorScope` — can resolve the reference an author actually writes. A watch
// path wired to a resolver set that does not carry `env` would pass every test in that file
// and fail on the first secured suite a developer saves.
//
// THIS FILE IS A REPLICA, AND THE WORD IS EXACT. It never references WatchRunner — it cannot:
// WatchRunner is internal to the `vouchfx` assembly, and the composition happens inside the build
// lambda CreateSession wires up. What is executed here is a HAND-ASSEMBLED copy of those two
// statements, so what these tests prove is that the composition BEHAVES correctly, not that
// WatchRunner performs it. That second half rests entirely on a textual census in a different
// assembly — Vouchfx.Cli.Tests/WatchRunnerSecurityLedgerTests, which pins the call site and
// forbids the accessor argument being dropped again. Neither half stands alone; read them as a
// pair, and if the census is ever deleted this file stops being evidence about `--watch` at all.
//
// The split is forced rather than chosen, and the ASSEMBLY BOUNDARY is what forces it — not the
// missing seam #364 named, which is now closed. SecurityConfigurationAccessor.Build and
// ScenarioRunner.CreateSecretAccessorScope are internal to Vouchfx.Engine.Runtime (visible here,
// not to Vouchfx.Cli.Tests), while WatchRunner is internal to `vouchfx` (visible there, not
// here). No single test project can both execute the composition and read the call site.
//
// WHAT IS *NOT* COVERED HERE, AND WHY. What follows the two lines above in WatchRunner is the
// injected topology starter, which in production is `TopologyRequest.StartAsync` and needs Docker;
// the connection the probe then makes is outside any Docker-free reach. It does not need to be
// here: the accessor decides the identity BEFORE any connection, so "loads the identity" and
// "refuses with EDGE-001's diagnostic instead of connecting anonymously" are both settled at this
// boundary.
//
// Non-Docker. Certificates are generated in-process by TestCertificateAuthority and written to
// a temp suite directory, so the REAL PEM load path runs — including the PKCS#12 round trip.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Runtime;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// EDGE-007 tests for the client identity the <c>--watch</c> topology probe loads: an encrypted
/// client key plus a correct <c>clientKeyPassword</c> yields a usable identity, and a wrong one
/// raises EDGE-001's diagnostic rather than degrading to a no-identity connection.
/// </summary>
public sealed class WatchProbeSecurityWiringTests
{
    /// <summary>
    /// EDGE-007, first half: a suite declaring <c>clientKeyPassword</c> loads the client identity
    /// when the probe's accessor is built the way <c>WatchRunner</c> builds it. USABLE means the
    /// private key survived the accessor's PKCS#12 round trip — a certificate that reports
    /// <see cref="X509Certificate2.HasPrivateKey"/> and then fails the handshake is not an identity.
    /// </summary>
    [Fact]
    public void ForTheWatchProbeComposition_EncryptedKeyAndCorrectPassphrase_LoadsTheClientIdentity()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, "pilot-passphrase");
        try
        {
            // The session ledger EDGE-007's seam hoists in WatchRunner.RunAsync. Passing it here
            // is not incidental to this arm: it is the object the resolved passphrase is recorded
            // into, and the assertion at the end is what makes the run seam's scrub reachable.
            var sessionLedger = new ResolvedSecretLedger();
            using var probeSecrets = ScenarioRunner.CreateSecretAccessorScope(sessionLedger);

            var probeSecurity = SecurityConfigurationAccessor.Build(
                AstWithSecuredService("payments", MtlsSecurityWithPassphrase(EnvReference(variable))),
                bed.SuiteDirectory,
                probeSecrets.Accessor, pathDisclosures: null);
            try
            {
                var certificates = probeSecurity.For("payments")!.Certificates!;

                Assert.NotNull(certificates.ClientCertificate);
                Assert.True(certificates.ClientCertificate!.HasPrivateKey);
                Assert.Equal(
                    TestCertificateAuthority.ClientSubjectCommonName,
                    certificates.ClientCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));

                // The passphrase was ACTUALLY USED, not merely accepted: an unused passphrase
                // would leave the ledger empty, and the encrypted key would not have loaded at
                // all. This is also the fact the run seam's scrub depends on — the probe records
                // into the SESSION ledger, so a value resolved on save 1 is scrubbable from text
                // emitted on save 5.
                Assert.Equal(1, sessionLedger.Count);
            }
            finally
            {
                (probeSecurity as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// EDGE-007, second half: a WRONG passphrase on the watch composition produces EDGE-001's
    /// diagnostic — naming <c>clientKeyPassword</c> as the likely cause and echoing neither the
    /// value nor its length — rather than resolving to a null certificate, which is this engine's
    /// wire signal for "present no client identity" and would connect anonymously.
    /// </summary>
    [Fact]
    public void ForTheWatchProbeComposition_WrongPassphrase_RaisesEdge001RatherThanConnectingAnonymously()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "the-right-passphrase");

        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, "the-wrong-passphrase");
        try
        {
            using var probeSecrets =
                ScenarioRunner.CreateSecretAccessorScope(new ResolvedSecretLedger());

            var probeSecurity = SecurityConfigurationAccessor.Build(
                AstWithSecuredService("payments", MtlsSecurityWithPassphrase(EnvReference(variable))),
                bed.SuiteDirectory,
                probeSecrets.Accessor, pathDisclosures: null);
            try
            {
                var certificates = probeSecurity.For("payments")!.Certificates!;

                // THROWS — it does not return null. That distinction is the whole of EDGE-007's
                // second half: a null here is indistinguishable from `profile: tls` and the probe
                // would complete a connection presenting nothing.
                var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

                Assert.Contains("clientKeyPassword", ex.Message, StringComparison.Ordinal);
                Assert.Contains("the passphrase is wrong", ex.Message, StringComparison.Ordinal);

                // Neither value — the one that was set, nor the one the key actually takes.
                Assert.DoesNotContain("the-wrong-passphrase", ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("the-right-passphrase", ex.Message, StringComparison.Ordinal);

                // …and not its LENGTH either, which is an oracle in its own right.
                Assert.DoesNotContain("length", ex.Message, StringComparison.OrdinalIgnoreCase);

                // The platform's fault is preserved rather than swallowed.
                Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
            }
            finally
            {
                (probeSecurity as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// The structural half of EDGE-007's evidence must still exist. The reciprocal of the guard in
    /// <c>Vouchfx.Cli.Tests.WatchRunnerSecurityLedgerTests</c>, so neither half can be deleted
    /// while the other stays green.
    /// </summary>
    /// <remarks>
    /// This file executes a REPLICA and never references <c>WatchRunner</c> — it cannot, across
    /// the assembly boundary. Everything tying the replica to the real call site lives in the
    /// census file: that the probe's accessor comes from the production factory, that the ledger
    /// is session-scoped, that both consumers receive it. Delete that file and these two tests go
    /// on passing while proving nothing whatsoever about <c>--watch</c>, which is precisely the
    /// silent-loss-of-coverage failure EDGE-007 exists to close.
    /// </remarks>
    [Fact]
    public void TheStructuralHalfOfTheEvidence_StillExists()
    {
        var census = Path.Combine(
            RepositoryRoot(), "tests", "Vouchfx.Cli.Tests", "WatchRunnerSecurityLedgerTests.cs");

        Assert.True(
            File.Exists(census),
            $"'{census}' is missing. It is the only thing connecting this file's replica of the "
            + "probe composition to WatchRunner's actual call site; without it these tests prove "
            + "that the composition works, not that --watch performs it.");

        // Not merely present — still carrying the census that pins the call site.
        var text = File.ReadAllText(census);
        Assert.Contains(
            "TheWatchProbe_BuildsItsSecurityAccessorFromTheProductionSecretFactory",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "TheWatchSession_SharesOneLedgerBetweenTheProbeAndTheStepPath",
            text,
            StringComparison.Ordinal);
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the test binary to the repository root (the directory holding
    /// <c>vouchfx.sln</c>) — the same discovery shape this repo's other file-reading gates use.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "could not locate the repository root (no ancestor directory of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').");
    }
    //
    // Deliberately local rather than shared with SecurityConfigurationAccessorTests: that class's
    // equivalents are private to it, and lifting them into a shared helper would couple the watch
    // path's coverage to a file whose own arms are free to change shape.

    private static SecuritySpec MtlsSecurityWithPassphrase(string reference) =>
        new(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: TestCertificateAuthority.ClientCertFileName,
            ClientKey: TestCertificateAuthority.ClientKeyFileName,
            ServerArtifacts: null)
        {
            ClientKeyPassword = reference,
        };

    private static string EnvReference(string variable) => "${secret:env/" + variable + "}";

    private static ScenarioAst AstWithSecuredService(string serviceName, SecuritySpec security) =>
        new(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
                {
                    [serviceName] = new ServiceSpec(
                        Image: "acme/payments:1.2",
                        Project: null,
                        ImagePullPolicy: null,
                        HttpPort: null,
                        Env: null)
                    {
                        Security = security,
                    },
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());
}
