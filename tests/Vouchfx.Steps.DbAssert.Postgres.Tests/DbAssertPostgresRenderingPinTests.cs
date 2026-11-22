// Pins what db-assert.postgres compares and reports for PostgreSQL date/time and other
// "driver-rendered" column types on Npgsql 9.0.5, so that a later driver major shows exactly what
// it changes (the dependency update policy holds Npgsql above 9 for this reason). The same pins
// passed unchanged on 8.0.9, the version before the move to 9.x.
//
// The emitted helper reads each column with the non-generic reader[col] and compares
// value.ToString() ordinally with the suite's expect.row text. These tests run the REAL emitted
// step against a loopback fake of the PostgreSQL wire protocol (no Docker), returning binary-format
// values encoded as the server encodes them. Their Docker-backed twin
// (DbAssertPostgresRenderingPinDockerTests) runs the same matrix against a real server.
//
// What is pinned is TODAY'S behaviour, including behaviour that is arguably a defect. Those are
// listed once, as R1-R6, in the header of PostgresRenderCases.cs, and every test here that pins one
// carries [Trait("pins", "defect")]. Do not "fix" an expectation here without deciding that the
// engine's behaviour is meant to change.
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
        foreach (var (sql, column) in PostgresRenderCases.ExtraResults)
        {
            results[sql] = new[] { column };
        }

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
    [Trait("pins", "defect")]
    public async Task CultureDependence_ASuiteWrittenUnderOneCulture_FailsUnderAnother()
    {
        // The recorded DEFECT R1, stated as a behaviour: the same database value renders differently
        // per process culture, so an expect.row written on one machine can fail on another. Nothing
        // in the provider fixes the culture.
        var date = PostgresRenderCases.Find("date / single-digit day and month");
        var connection = _fixture.ConnectionString();
        var step = PostgresStepHarness.CompiledFor(date);

        var enGb = date.RenderedUnder("en-GB");
        Assert.Equal("05/01/2024 00:00:00", enGb);

        var underEnGb = await PostgresStepHarness.UnderCultureAsync("en-GB", () => step.RunAsync(
            connection, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = enGb }));
        Assert.True(
            underEnGb.Verdict == Verdict.Pass,
            $"Under en-GB the en-GB text '{enGb}' was expected to pass; observed {underEnGb.Verdict}: {underEnGb.Observation}");

        foreach (var other in s_otherCultures)
        {
            var underOther = await PostgresStepHarness.UnderCultureAsync(other, () => step.RunAsync(
                connection, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = enGb }));
            Assert.True(
                underOther.Verdict == Verdict.Fail,
                $"Under culture '{other}' the en-GB text '{enGb}' was expected to FAIL (the recorded culture dependence); " +
                $"observed {underOther.Verdict}: {underOther.Observation}");
        }
    }

    [Fact]
    [Trait("pins", "defect")]
    public async Task FractionalSeconds_AreDropped_SoADifferentMicrosecondValuePasses()
    {
        // The recorded DEFECT R2: a timestamp's text has no fractional seconds, so an expect.row cannot
        // tell 13:45:30.123456 from 13:45:30.654321, and the text written for one passes for the other.
        var first = PostgresRenderCases.Find("timestamp / fractional seconds");
        var connection = _fixture.ConnectionString();
        var text = first.RenderedUnder(string.Empty);
        Assert.Equal("01/05/2024 13:45:30", text);

        var otherText = await PostgresStepHarness.UnderCultureAsync(
            string.Empty,
            () => PostgresStepHarness.RenderedAsync(PostgresRenderCases.TimestampOtherMicrosecondsSql, connection));
        Assert.Equal(text, otherText);

        foreach (var sql in new[] { first.Sql, PostgresRenderCases.TimestampOtherMicrosecondsSql })
        {
            var outcome = await PostgresStepHarness.UnderCultureAsync(
                string.Empty,
                () => PostgresStepHarness.CompiledForQuery(sql).RunAsync(
                    connection, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = text }));
            Assert.True(
                outcome.Verdict == Verdict.Pass,
                $"'{text}' was expected to pass for {sql} (different microseconds, same text); observed {outcome.Verdict}: {outcome.Observation}");
        }
    }

    [Fact]
    [Trait("pins", "defect")]
    public async Task ByteaAndArrays_RenderAsClrTypeNames_TheirContentIsNeverCompared()
    {
        // The recorded DEFECT R3: the text is the CLR type name, so two byteas with different content
        // satisfy the same expect.row, and an array's elements never appear in it.
        var connection = _fixture.ConnectionString();
        var expectations = new[]
        {
            (PostgresRenderCases.Find("bytea / value").Sql, "System.Byte[]"),
            (PostgresRenderCases.ByteaOtherContentSql, "System.Byte[]"),
            (PostgresRenderCases.Find("integer[] / value").Sql, "System.Int32[]"),
        };

        foreach (var (sql, text) in expectations)
        {
            var outcome = await PostgresStepHarness.CompiledForQuery(sql).RunAsync(
                connection, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = text });
            Assert.True(
                outcome.Verdict == Verdict.Pass,
                $"'{text}' was expected to pass for {sql}; observed {outcome.Verdict}: {outcome.Observation}");
        }
    }

    [Fact]
    [Trait("pins", "defect")]
    public async Task Interval_WithAMonthsComponent_IsIndistinguishableFromSqlNull()
    {
        // The recorded DEFECT R4: Npgsql 8 and 9 cannot return an interval with months as a TimeSpan, the
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
    [Trait("pins", "defect")]
    public async Task TimeTz_Text_CarriesAMeaninglessDatePart()
    {
        // The recorded DEFECT R5: a time with time zone is read as a DateTimeOffset on 0001-01-02, so
        // the text starts with a date nothing in the database says, and the time-and-offset an author
        // would expect (PostgreSQL's own spelling) does not match.
        var timeTz = PostgresRenderCases.Find("time with time zone / fractional seconds, +02");
        var invariant = timeTz.RenderedUnder(string.Empty);
        Assert.StartsWith("01/02/0001 ", invariant, StringComparison.Ordinal);

        var outcome = await PostgresStepHarness.UnderCultureAsync(
            string.Empty,
            () => PostgresStepHarness.CompiledFor(timeTz).RunAsync(
                _fixture.ConnectionString(),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = timeTz.Alternative }));
        Assert.True(
            outcome.Verdict == Verdict.Fail,
            $"The server's own spelling '{timeTz.Alternative}' was expected to fail; observed {outcome.Verdict}: {outcome.Observation}");
    }

    [Fact]
    [Trait("pins", "defect")]
    public async Task UnreadableValueAndMisspeltColumn_BothRenderAsNull()
    {
        // The recorded DEFECT R6: the emitted helper catches every exception from reader[column] and
        // compares the text "null", so an unreadable value (an interval with months) and a column name
        // that does not exist (a misspelling of "v") both pass expect.row "null". The control shows the
        // same step reports the real text when the column is spelt right.
        var connection = _fixture.ConnectionString();
        var timestamp = PostgresRenderCases.Find("timestamp / fractional seconds");
        var withMonths = PostgresRenderCases.Find("interval / months, days and time");
        var nullExpected = new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = "null" };

        var control = await PostgresStepHarness.CompiledFor(timestamp).RunAsync(connection, nullExpected);
        Assert.Equal(Verdict.Fail, control.Verdict);

        var misspelt = await PostgresStepHarness.CompiledForQuery(timestamp.Sql, "vv").RunAsync(connection, nullExpected);
        Assert.True(
            misspelt.Verdict == Verdict.Pass,
            $"A misspelt column ('vv' for 'v') with expect 'null' was expected to pass; observed {misspelt.Verdict}: {misspelt.Observation}");

        var misspeltOther = await PostgresStepHarness.CompiledForQuery(timestamp.Sql, "vv").RunAsync(
            connection, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = "01/05/2024 13:45:30" });
        Assert.Equal("null", PostgresStepHarness.ActualOf(misspeltOther));

        var unreadable = await PostgresStepHarness.CompiledFor(withMonths).RunAsync(connection, nullExpected);
        Assert.Equal(Verdict.Pass, unreadable.Verdict);
    }

    [Fact]
    public async Task CultureDependence_MeasuredFromRealRenders_IsLimitedToTheTypesBackedByCultureSensitiveClrFormatting()
    {
        // Which pinned types vary with culture (date/timestamp-like values and numeric), and which do
        // not (everything else), computed from what the step actually reports under each culture, so a
        // driver that changed a type's text would move this set instead of leaving a table to agree with itself.
        var connection = _fixture.ConnectionString();
        var dependent = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var renderCase in PostgresRenderCases.All)
        {
            var texts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var culture in PostgresRenderCases.Cultures)
            {
                texts.Add(PostgresStepHarness.NormaliseSpaces(await PostgresStepHarness.UnderCultureAsync(
                    culture, () => PostgresStepHarness.RenderedAsync(renderCase, connection))));
            }

            if (texts.Count > 1)
            {
                dependent.Add(renderCase.Type);
            }
        }

        Assert.Equal(s_cultureDependentTypes, dependent.ToArray());
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
