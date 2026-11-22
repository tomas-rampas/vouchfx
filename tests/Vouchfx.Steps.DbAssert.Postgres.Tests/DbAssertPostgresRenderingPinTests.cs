// Pins what db-assert.postgres compares and reports for PostgreSQL date/time and other
// "driver-rendered" column types on Npgsql 8.0.9, so that a later driver major shows exactly what
// it changes (the dependency update policy holds Npgsql above 8 for this reason).
//
// The emitted helper reads each column with the non-generic reader[col] and compares
// value.ToString() ordinally with the suite's expect.row text. These tests run the REAL emitted
// step against a loopback fake of the PostgreSQL wire protocol (no Docker), returning binary-format
// values encoded as the server encodes them. Their Docker-backed twin
// (DbAssertPostgresRenderingPinDockerTests) runs the same matrix against a real server.
//
// What is pinned is TODAY'S behaviour, including behaviour that is arguably a defect (culture
// dependence, dropped fractional seconds, "System.Byte[]"). Do not "fix" an expectation here
// without deciding that the engine's behaviour is meant to change.
using System.Globalization;
using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

/// <summary>Starts the wire fake with every rendering case's canned row.</summary>
public sealed class PostgresWireFakeFixture : IDisposable
{
    private readonly PostgresWireFake _fake;

    public PostgresWireFakeFixture()
    {
        var results = PostgresRenderCases.All.ToDictionary(
            c => c.Sql,
            c => (IReadOnlyList<FakeColumn>?)new[] { new FakeColumn("v", c.Oid, c.Binary()) },
            StringComparer.Ordinal);
        _fake = new PostgresWireFake(sql => results.TryGetValue(sql, out var row) ? row : null);
    }

    internal PostgresWireFake Fake => _fake;

    /// <summary>
    /// Connection string for the fake. Pooling is off so every run opens a fresh connection (and so
    /// sends a startup message); "NoTypeLoading" skips the pg_type catalogue query the fake does not
    /// answer. Neither changes how a value is read or rendered.
    /// </summary>
    public string ConnectionString(string extra = "") =>
        $"Host=127.0.0.1;Port={_fake.Port};Username=u;Password=p;Database=d;Pooling=false;" +
        "SSL Mode=Disable;Server Compatibility Mode=NoTypeLoading" + extra;

    public void Dispose() => _fake.Dispose();
}

[Collection(PostgresPinCollectionDefinition.Name)]
public sealed class DbAssertPostgresRenderingPinTests : IClassFixture<PostgresWireFakeFixture>
{
    private static readonly string[] s_cultureDependentTypes =
        { "date", "numeric", "time with time zone", "timestamp", "timestamp with time zone" };

    private static readonly short[] s_binary = { 1 };

    private static readonly string[] s_otherCultures = { "en-US", "cs-CZ", string.Empty };

    private readonly PostgresWireFakeFixture _fixture;

    public DbAssertPostgresRenderingPinTests(PostgresWireFakeFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> CaseTheory() => PostgresRenderCases.Theory();

    public static IEnumerable<object[]> CaseByCultureTheory() =>
        PostgresRenderCases.All.SelectMany(c =>
            PostgresRenderCases.Cultures.Select(culture => new object[] { c.ToString(), culture }));

    [Fact]
    public void Cultures_UsedByThePins_AreBackedByGlobalizationData()
    {
        // The per-culture literals below need real culture data. Under invariant-globalization mode
        // (DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, or no ICU) every culture formats like the
        // invariant culture and this assertion names the cause instead of a confusing text diff.
        Assert.Equal(",", CultureInfo.GetCultureInfo("cs-CZ").NumberFormat.NumberDecimalSeparator);
        Assert.Equal("dd/MM/yyyy", CultureInfo.GetCultureInfo("en-GB").DateTimeFormat.ShortDatePattern);
    }

    [Theory]
    [MemberData(nameof(CaseTheory))]
    public async Task ClrType_ReturnedByNpgsql8_IsPinned(string caseName)
    {
        await PostgresStepHarness.AssertClrTypeAsync(PostgresRenderCases.Find(caseName), _fixture.ConnectionString());
    }

    [Theory]
    [MemberData(nameof(CaseByCultureTheory))]
    public async Task RenderedText_UnderEachCulture_IsPinned(string caseName, string culture)
    {
        await PostgresStepHarness.AssertRenderingAsync(PostgresRenderCases.Find(caseName), culture, _fixture.ConnectionString());
    }

    [Fact]
    public async Task Npgsql_RequestsBinaryFormat_ForEveryResultColumn()
    {
        // The fake only speaks binary; this proves Npgsql asked for it (so the cases above exercise the
        // binary decode path a real server's rows take) rather than silently falling back to text.
        var renderCase = PostgresRenderCases.All[0];
        await PostgresStepHarness.RenderedAsync(renderCase, _fixture.ConnectionString());

        Assert.All(_fixture.Fake.ResultFormats, formats => Assert.Equal(s_binary, formats));
    }

    [Fact]
    public async Task CultureDependence_ASuiteWrittenUnderOneCulture_FailsUnderAnother()
    {
        // The documented DEFECT, stated as a behaviour: the same database value renders differently
        // per process culture, so an expect.row written on one machine can fail on another. Nothing
        // in the provider fixes the culture.
        var date = PostgresRenderCases.Find("date / single-digit day and month");
        var connection = _fixture.ConnectionString();
        var step = PostgresStepHarness.CompiledFor(date);

        var enGb = date.RenderedUnder("en-GB");
        Assert.Equal("05/01/2024 00:00:00", enGb);

        var underEnGb = await PostgresStepHarness.UnderCultureAsync("en-GB", () => step.RunAsync(
            connection, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = enGb }));
        Assert.Equal(Verdict.Pass, underEnGb.Verdict);

        foreach (var other in s_otherCultures)
        {
            var underOther = await PostgresStepHarness.UnderCultureAsync(other, () => step.RunAsync(
                connection, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = enGb }));
            Assert.Equal(Verdict.Fail, underOther.Verdict);
        }
    }

    [Fact]
    public async Task Interval_WithAMonthsComponent_IsIndistinguishableFromSqlNull()
    {
        // The documented DEFECT: Npgsql 8 cannot return an interval with months as a TimeSpan, the
        // helper swallows that read failure as null, and "null" is also the text it uses for SQL NULL.
        // So expect.row "null" passes for an interval that has a value.
        var withMonths = PostgresRenderCases.Find("interval / months, days and time");
        var sqlNull = PostgresRenderCases.Find("interval / NULL");

        foreach (var renderCase in new[] { withMonths, sqlNull })
        {
            var outcome = await PostgresStepHarness.CompiledFor(renderCase).RunAsync(
                _fixture.ConnectionString(),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = "null" });
            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
    }

    [Fact]
    public void CultureDependence_IsLimitedToTheTypesBackedByCultureSensitiveClrFormatting()
    {
        // Which pinned types vary with culture (date/timestamp-like values and numeric), and which do
        // not (everything else), read straight off the pinned table.
        var dependent = PostgresRenderCases.All
            .Where(c => c.Rendered.Values.Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(c => c.Type)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(s_cultureDependentTypes, dependent);
    }
}

/// <summary>
/// One serial collection for the pin tests that mutate process-wide state (current culture, the
/// TZ environment variable) or start containers, so they cannot interleave with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresPinCollectionDefinition
{
    public const string Name = "db-assert-postgres-npgsql-pins";
}
