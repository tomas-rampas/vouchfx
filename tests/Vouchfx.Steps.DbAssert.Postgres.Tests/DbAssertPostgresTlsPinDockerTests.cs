// Pins what db-assert.postgres does today for TLS server-certificate validation, on Npgsql 8.0.9,
// against real PostgreSQL servers started with ssl=on and certificates generated at test time. It
// records today's behaviour; it is not a regression net for the Npgsql 10 TLS change (see below).
//
// What a suite can set. The provider passes the connection string it finds in Vars straight to
// new NpgsqlConnection(...); it adds, parses and validates nothing of its own. For an engine-managed
// postgres dependency that string is Aspire's (host, port, user, password, database: no SSL option),
// and the DSL has no field to add one: a postgres dependency cannot carry a `security` block (the
// language schema restricts `security` to kafka dependencies and services; DSL specification
// section 3.2.6b), and the provider uses the engine's own connection string as it finds it. So a
// suite cannot reach any setting below today. The "settings" are Npgsql's own connection-string
// keywords, pinned because a driver major changes what they mean, and because the default
// connection string is the one every managed postgres dependency actually uses.
//
// What each cell records is the step's verdict and diagnostic:
//   tls    Pass, and pg_stat_ssl confirms the session is encrypted.
//   plain  Pass, and pg_stat_ssl confirms the session is NOT encrypted.
//   deny   EnvironmentError "Exception while performing SSL handshake" (the same text whatever the cause).
//   nossl  EnvironmentError "SSL connection requested. No SSL enabled connection from this host is configured."
//          (Npgsql's wording for a server that refuses the SSL request).
//
// What the matrix pins, and what it cannot show. It pins the step's verdict and diagnostic for each
// setting against six server shapes, every one of which uses a PRIVATE certificate authority that no
// system trust store knows. It is expected to read the same on Npgsql 10: a reviewer ran it on
// 10.0.4 on 22 November 2026 and all of it passed unchanged. That is why it is NOT the regression
// net for the TLS change that holds the driver back (.github/dependabot.yml). By a reviewer's
// reading of the driver source, what Npgsql 10 changed in the root validation fires only when the
// server certificate is already trusted by the SYSTEM store, so no cell here reaches it. The case that
// would show the change is "the server certificate is trusted by the system store while the suite
// provides a different root" (SSL Mode=VerifyFull with a Root Certificate: Npgsql 8 connects over TLS,
// Npgsql 10 refuses, on the reviewer's reading). Covering it needs a system trust store the test can
// control, which this suite does not have; the change that upgrades the driver must cover that case.
//
// Lanes. These tests need Docker and run only in the daily Docker lane (requires=docker), not in the
// PR lanes: a driver-upgrade branch must run that lane by manual dispatch before it is merged. The
// fixture also sweeps this process's certificate residue from the Windows intermediate-CA stores
// when it is disposed (see PostgresTlsFixture), because the non-Docker guard cannot see a run that
// generated these certificates.
//
// RECORDED TLS WEAKNESSES. Pinned as today's behaviour, NOT endorsed, and not fixed here. Each has a
// test marked [Trait("pins", "defect")], so `dotnet test --list-tests --filter "pins=defect"` lists
// them with the rendering defects R1-R6 (listed in the header of PostgresRenderCases.cs), and the
// affected matrix rows end with "// defect".
//
//   T1  One diagnostic for different causes.
//       What: an unknown authority, a missing intermediate, a name that does not match the host, and
//             a private authority checked against the system roots all report the same text, so a
//             suite author cannot tell them apart from the verdict output.
//       Pinned by: EveryValidationRefusal_ReportsTheSameDiagnostic_WhateverTheCause
//   T2  INSECURE, pinned as current behaviour, not endorsed: the default connection encrypts without validating.
//       What: the connection string the engine builds carries no SSL Mode, which Npgsql 8 treats as
//             Prefer: it uses TLS when the server offers it and accepts ANY certificate, including one
//             from an unknown authority or for another host. An attacker in the path is not detected.
//       Pinned by: DefaultConnectionString_Insecure_EncryptsWithoutValidatingTheCertificate
//   T3  INSECURE, pinned as current behaviour, not endorsed: SSL Mode=Require ignores Root Certificate.
//       What: a suite that provides a root and asks for Require gets no validation at all; the root is
//             silently unused (only VerifyCA and VerifyFull use it).
//       Pinned by: Require_Insecure_IgnoresAProvidedRootCertificate
using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

public enum TlsCell
{
    Tls,
    Plain,
    Deny,
    NoSsl,
}

[Collection(PostgresPinCollectionDefinition.Name)]
[Trait("requires", "docker")]
public sealed class DbAssertPostgresTlsPinDockerTests : IClassFixture<PostgresTlsFixture>
{
    private const string HandshakeFailure = "{\"error\":\"Exception while performing SSL handshake\"}";
    private const string NoServerSupport = "{\"error\":\"SSL connection requested. No SSL enabled connection from this host is configured.\"}";

    // Column order of every row below.
    private static readonly TlsServerShape[] s_shapes =
    {
        TlsServerShape.DirectUnderProvidedRoot,
        TlsServerShape.ThroughIntermediateSentByServer,
        TlsServerShape.ThroughIntermediateNotSent,
        TlsServerShape.UnknownAuthority,
        TlsServerShape.NameDoesNotMatchHost,
        TlsServerShape.ServerWithoutTls,
    };

    private const string Provided = "{root}";

    // Setting names the tests below refer to (each is also a matrix row's name).
    private const string SettingDefault = "no SSL Mode (what the engine's own connection string gives)";
    private const string SettingDisable = "SSL Mode=Disable";
    private const string SettingRequireIgnoringRoot = "SSL Mode=Require; Root Certificate (ignored: no validation)";
    private const string SettingVerifyCaRoot = "SSL Mode=VerifyCA; Root Certificate";
    private const string SettingVerifyFullRoot = "SSL Mode=VerifyFull; Root Certificate";
    private const string SettingVerifyFullSystemRoots = "SSL Mode=VerifyFull, no Root Certificate (system roots)";

    /// <summary>Setting name, connection-string suffix ("{root}" is the root PEM the suite provides), and the six cells.</summary>
    private static readonly (string Setting, string Suffix, TlsCell[] Cells)[] s_matrix =
    {
        (SettingDefault, string.Empty, // defect T2: INSECURE, see the header list
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Plain }),
        (SettingDisable, ";SSL Mode=Disable",
            new[] { TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain }),
        ("SSL Mode=Allow", ";SSL Mode=Allow",
            new[] { TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain }),
        ("SSL Mode=Prefer", ";SSL Mode=Prefer",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Plain }),
        ("SSL Mode=Require", ";SSL Mode=Require",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.NoSsl }),
        ("SSL Mode=Require; Trust Server Certificate=true", ";SSL Mode=Require;Trust Server Certificate=true",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.NoSsl }),
        (SettingRequireIgnoringRoot, ";SSL Mode=Require;Root Certificate=" + Provided, // defect T3: INSECURE, see the header list
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.NoSsl }),
        (SettingVerifyCaRoot, ";SSL Mode=VerifyCA;Root Certificate=" + Provided,
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Deny, TlsCell.Deny, TlsCell.Tls, TlsCell.NoSsl }),
        (SettingVerifyFullRoot, ";SSL Mode=VerifyFull;Root Certificate=" + Provided,
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Deny, TlsCell.Deny, TlsCell.Deny, TlsCell.NoSsl }),
        (SettingVerifyFullSystemRoots, ";SSL Mode=VerifyFull",
            new[] { TlsCell.Deny, TlsCell.Deny, TlsCell.Deny, TlsCell.Deny, TlsCell.Deny, TlsCell.NoSsl }),
        ("SSL Mode=VerifyCA; Root Certificate; Trust Server Certificate=true (does not downgrade)",
            ";SSL Mode=VerifyCA;Root Certificate=" + Provided + ";Trust Server Certificate=true",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Deny, TlsCell.Deny, TlsCell.Tls, TlsCell.NoSsl }),
        ("SSL Mode=VerifyFull; Root Certificate; Trust Server Certificate=true (does not downgrade)",
            ";SSL Mode=VerifyFull;Root Certificate=" + Provided + ";Trust Server Certificate=true",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Deny, TlsCell.Deny, TlsCell.Deny, TlsCell.NoSsl }),
    };

    private readonly PostgresTlsFixture _fixture;

    public DbAssertPostgresTlsPinDockerTests(PostgresTlsFixture fixture) => _fixture = fixture;

    /// <summary>Each row is (setting text, server shape, expected cell), so a failure reads as the case it is.</summary>
    public static IEnumerable<object[]> MatrixTheory() =>
        s_matrix.SelectMany(row => s_shapes.Select((shape, c) => new object[] { row.Setting, shape, row.Cells[c] }));

    private static readonly CompiledPostgresStep s_sslStep = PostgresStepHarness.Compile(
        "tls",
        "SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["ssl"] = "{expected}" });

    private async Task<StepOutcome> RunAsync(string setting, TlsServerShape shape, string expectedSsl)
    {
        var server = _fixture.Server(shape);
        var suffix = s_matrix.Single(m => m.Setting == setting).Suffix
            .Replace(Provided, _fixture.ProvidedRootPath(shape), StringComparison.Ordinal);
        return await s_sslStep.RunAsync(
            server.ConnectionString(suffix),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = expectedSsl });
    }

    [Theory]
    [MemberData(nameof(MatrixTheory))]
    public async Task Setting_AgainstServerCertificate_HasPinnedVerdictAndDiagnostic(string setting, TlsServerShape shape, TlsCell cell)
    {
        var outcome = cell switch
        {
            TlsCell.Plain => await RunAsync(setting, shape, "False"),
            _ => await RunAsync(setting, shape, "True"),
        };

        switch (cell)
        {
            case TlsCell.Tls:
            case TlsCell.Plain:
                Assert.Equal(Verdict.Pass, outcome.Verdict);
                Assert.Equal("{\"rowCount\":1}", outcome.Observation);
                break;
            case TlsCell.Deny:
                Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
                Assert.Equal(HandshakeFailure, outcome.Observation);
                break;
            default:
                Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
                Assert.Equal(NoServerSupport, outcome.Observation);
                break;
        }
    }

    [Fact]
    public async Task PlainCell_IsNotMerelyAnUnencryptedPass_ItFailsWhenEncryptionIsExpected()
    {
        // Guards the matrix against a vacuous reading: under SSL Mode=Disable the session really is
        // unencrypted, so an expectation of ssl=True is a Fail (a verdict, not an environment error).
        var outcome = await RunAsync(SettingDisable, TlsServerShape.DirectUnderProvidedRoot, "True");

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("{\"column\":\"ssl\",\"expected\":\"True\",\"actual\":\"False\"}", outcome.Observation);
    }

    [Fact]
    [Trait("pins", "defect")]
    public async Task EveryValidationRefusal_ReportsTheSameDiagnostic_WhateverTheCause()
    {
        // The recorded weakness T1. The step reports Npgsql's exception message only, and that message is
        // the same text for an unknown authority, a missing intermediate, a name mismatch and a private
        // authority checked against the system roots: a suite author cannot tell them apart from the
        // verdict output. Pinned as today's behaviour, not as a good one.
        var causes = new (string Setting, TlsServerShape Shape)[]
        {
            (SettingVerifyFullRoot, TlsServerShape.ThroughIntermediateNotSent),
            (SettingVerifyFullRoot, TlsServerShape.UnknownAuthority),
            (SettingVerifyFullRoot, TlsServerShape.NameDoesNotMatchHost),
            (SettingVerifyFullSystemRoots, TlsServerShape.DirectUnderProvidedRoot),
        };

        var observations = new List<string>();
        foreach (var (setting, shape) in causes)
        {
            var outcome = await RunAsync(setting, shape, "True");
            Assert.True(
                outcome.Verdict == Verdict.EnvironmentError,
                $"{setting} against {shape}: expected a refusal; observed {outcome.Verdict}: {outcome.Observation}");
            observations.Add(outcome.Observation!);
        }

        Assert.Single(observations.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("pins", "defect")]
    public async Task DefaultConnectionString_Insecure_EncryptsWithoutValidatingTheCertificate()
    {
        // The recorded weakness T2. INSECURE, pinned as current behaviour, not endorsed: with no SSL Mode
        // (the engine's own connection string) the session is encrypted, yet a certificate from an unknown
        // authority and a certificate for another host are both accepted. The same shapes are refused as
        // soon as validation is asked for.
        foreach (var shape in new[] { TlsServerShape.UnknownAuthority, TlsServerShape.NameDoesNotMatchHost })
        {
            var accepted = await RunAsync(SettingDefault, shape, "True");
            Assert.True(
                accepted.Verdict == Verdict.Pass,
                $"Default connection string against {shape}: expected an encrypted, unvalidated pass; observed {accepted.Verdict}: {accepted.Observation}");

            var validated = await RunAsync(SettingVerifyFullRoot, shape, "True");
            Assert.True(
                validated.Verdict == Verdict.EnvironmentError,
                $"{SettingVerifyFullRoot} against {shape}: expected a refusal; observed {validated.Verdict}: {validated.Observation}");
        }
    }

    [Fact]
    [Trait("pins", "defect")]
    public async Task Require_Insecure_IgnoresAProvidedRootCertificate()
    {
        // The recorded weakness T3. INSECURE, pinned as current behaviour, not endorsed: SSL Mode=Require
        // with a Root Certificate accepts a server whose certificate that root did not issue, while
        // VerifyCA with the same root refuses it. The root is silently unused.
        var ignored = await RunAsync(SettingRequireIgnoringRoot, TlsServerShape.UnknownAuthority, "True");
        Assert.True(
            ignored.Verdict == Verdict.Pass,
            $"{SettingRequireIgnoringRoot}: expected an unvalidated pass; observed {ignored.Verdict}: {ignored.Observation}");

        var used = await RunAsync(SettingVerifyCaRoot, TlsServerShape.UnknownAuthority, "True");
        Assert.True(
            used.Verdict == Verdict.EnvironmentError,
            $"{SettingVerifyCaRoot}: expected a refusal; observed {used.Verdict}: {used.Observation}");
    }
}
