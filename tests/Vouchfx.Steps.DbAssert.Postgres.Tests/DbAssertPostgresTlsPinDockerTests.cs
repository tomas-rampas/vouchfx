// Pins what db-assert.postgres does today for TLS server-certificate validation, on Npgsql 8.0.9,
// against real PostgreSQL servers started with ssl=on and certificates generated at test time.
//
// What a suite can set. The provider passes the connection string it finds in Vars straight to
// new NpgsqlConnection(...); it adds, parses and validates nothing of its own. For an engine-managed
// postgres dependency that string is Aspire's (host, port, user, password, database: no SSL option),
// and the DSL has no field to add one (a security block is refused on postgres dependencies). So the
// "settings" below are Npgsql's own connection-string keywords, pinned because a driver major changes
// what they mean (Npgsql 10 validates against the provided root only; see .github/dependabot.yml).
//
// What each cell records is the step's verdict and diagnostic:
//   tls    Pass, and pg_stat_ssl confirms the session is encrypted.
//   plain  Pass, and pg_stat_ssl confirms the session is NOT encrypted.
//   deny   EnvironmentError "Exception while performing SSL handshake" (the same text whatever the cause).
//   nossl  EnvironmentError "SSL connection requested. No SSL enabled connection from this host is configured."
//          (Npgsql's wording for a server that refuses the SSL request).
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

    /// <summary>Setting name, connection-string suffix ("{root}" is the root PEM the suite provides), and the six cells.</summary>
    private static readonly (string Setting, string Suffix, TlsCell[] Cells)[] s_matrix =
    {
        ("no SSL Mode (what the engine's own connection string gives)", string.Empty,
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Plain }),
        ("SSL Mode=Disable", ";SSL Mode=Disable",
            new[] { TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain }),
        ("SSL Mode=Allow", ";SSL Mode=Allow",
            new[] { TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain, TlsCell.Plain }),
        ("SSL Mode=Prefer", ";SSL Mode=Prefer",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Plain }),
        ("SSL Mode=Require", ";SSL Mode=Require",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.NoSsl }),
        ("SSL Mode=Require; Trust Server Certificate=true", ";SSL Mode=Require;Trust Server Certificate=true",
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.NoSsl }),
        ("SSL Mode=Require; Root Certificate (ignored: no validation)", ";SSL Mode=Require;Root Certificate=" + Provided,
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.Tls, TlsCell.NoSsl }),
        ("SSL Mode=VerifyCA; Root Certificate", ";SSL Mode=VerifyCA;Root Certificate=" + Provided,
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Deny, TlsCell.Deny, TlsCell.Tls, TlsCell.NoSsl }),
        ("SSL Mode=VerifyFull; Root Certificate", ";SSL Mode=VerifyFull;Root Certificate=" + Provided,
            new[] { TlsCell.Tls, TlsCell.Tls, TlsCell.Deny, TlsCell.Deny, TlsCell.Deny, TlsCell.NoSsl }),
        ("SSL Mode=VerifyFull, no Root Certificate (system roots)", ";SSL Mode=VerifyFull",
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

    public static IEnumerable<object[]> MatrixTheory() =>
        s_matrix.SelectMany((row, r) => s_shapes.Select((shape, c) => new object[] { r, (int)shape, (int)row.Cells[c] }));

    private static readonly CompiledPostgresStep s_sslStep = PostgresStepHarness.Compile(
        "tls",
        "SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["ssl"] = "{expected}" });

    private async Task<StepOutcome> RunAsync(int row, TlsServerShape shape, string expectedSsl)
    {
        var server = _fixture.Server(shape);
        var suffix = s_matrix[row].Suffix.Replace(Provided, _fixture.ProvidedRootPath(shape), StringComparison.Ordinal);
        return await s_sslStep.RunAsync(
            server.ConnectionString(suffix),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = expectedSsl });
    }

    [Theory]
    [MemberData(nameof(MatrixTheory))]
    public async Task Setting_AgainstServerCertificate_HasPinnedVerdictAndDiagnostic(int row, int shapeValue, int cellValue)
    {
        var shape = (TlsServerShape)shapeValue;
        var cell = (TlsCell)cellValue;

        var outcome = cell switch
        {
            TlsCell.Plain => await RunAsync(row, shape, "False"),
            _ => await RunAsync(row, shape, "True"),
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
        var outcome = await RunAsync(1, TlsServerShape.DirectUnderProvidedRoot, "True");

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("{\"column\":\"ssl\",\"expected\":\"True\",\"actual\":\"False\"}", outcome.Observation);
    }

    [Fact]
    public async Task EveryValidationRefusal_ReportsTheSameDiagnostic_WhateverTheCause()
    {
        // The step reports Npgsql's exception message only, and that message is the same text for an
        // unknown authority, a missing intermediate and a name mismatch: a suite author cannot tell
        // them apart from the verdict output. Pinned as today's behaviour, not as a good one.
        var observations = new List<string>();
        foreach (var shape in new[]
                 {
                     TlsServerShape.ThroughIntermediateNotSent,
                     TlsServerShape.UnknownAuthority,
                     TlsServerShape.NameDoesNotMatchHost,
                 })
        {
            var verifyFull = Array.FindIndex(s_matrix, m => m.Setting.StartsWith("SSL Mode=VerifyFull; Root", StringComparison.Ordinal));
            var outcome = await RunAsync(verifyFull, shape, "True");
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            observations.Add(outcome.Observation!);
        }

        Assert.Single(observations.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Diagnostic_NeverContainsThePassword()
    {
        var server = _fixture.Server(TlsServerShape.UnknownAuthority);
        var verifyFull = Array.FindIndex(s_matrix, m => m.Setting.StartsWith("SSL Mode=VerifyFull; Root", StringComparison.Ordinal));
        var outcome = await RunAsync(verifyFull, TlsServerShape.UnknownAuthority, "True");

        Assert.DoesNotContain(server.Password, outcome.Observation, StringComparison.Ordinal);
    }
}
