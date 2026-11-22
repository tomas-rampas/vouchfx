// The real-server twin of DbAssertPostgresRenderingPinTests: the same value matrix, the same
// literals, run against a PostgreSQL container (the image the engine's postgres dependency uses), plus
// the session-time-zone pins that only a real server can answer.
//
// The container's connection string is this fixture's own (PostgresDockerContainer.ConnectionString:
// loopback host and published port, user postgres, Pooling=false so every run opens a fresh session),
// not the one the engine builds for a managed postgres dependency. Rendering does not depend on the
// difference. These pins run only in the daily Docker lane (requires=docker), not in the PR lanes.
//
// If a case passes against the fake but fails here, the fake's binary encoding is wrong; if it passes
// here but a later Npgsql major changes it, that is exactly the change this file exists to show.
using System.Globalization;
using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

[Collection(PostgresPinCollectionDefinition.Name)]
[Trait("requires", "docker")]
public sealed class DbAssertPostgresRenderingPinDockerTests : IClassFixture<PostgresPlainFixture>
{
    private readonly PostgresPlainFixture _fixture;

    public DbAssertPostgresRenderingPinDockerTests(PostgresPlainFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> CaseTheory() => PostgresRenderCases.Theory();

    public static IEnumerable<object[]> CaseByCultureTheory() =>
        PostgresRenderCases.All.SelectMany(c =>
            PostgresRenderCases.Cultures.Select(culture => new object[] { c.ToString(), culture }));

    [Theory]
    [MemberData(nameof(CaseTheory))]
    public async Task ClrType_ReturnedByNpgsql8_IsPinned(string caseName)
    {
        await PostgresStepHarness.AssertClrTypeAsync(
            PostgresRenderCases.Find(caseName), _fixture.Server.ConnectionString());
    }

    [Theory]
    [MemberData(nameof(CaseByCultureTheory))]
    public async Task RenderedText_UnderEachCulture_IsPinned(string caseName, string culture)
    {
        await PostgresStepHarness.AssertRenderingAsync(
            PostgresRenderCases.Find(caseName), culture, _fixture.Server.ConnectionString());
    }

    [Fact]
    public async Task SessionTimeZone_WhenTheConnectionStringSetsNone_IsTheServersOwnDefault()
    {
        // The provider adds no time zone, so a session simply inherits the server's setting.
        var step = PostgresStepHarness.Compile(
            "tz-default",
            "SHOW TimeZone",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["TimeZone"] = "Etc/UTC" });

        var outcome = await step.RunAsync(_fixture.Server.ConnectionString());

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/New_York")]
    public async Task TimestampTz_WithExplicitOffset_RendersTheSameUtcInstant_UnderAnySessionZone(string zone)
    {
        // The session zone does not change how a timestamptz is READ: Npgsql receives the UTC instant
        // and renders a DateTime of Kind Utc.
        var renderCase = PostgresRenderCases.Find("timestamp with time zone / fractional seconds, +02");

        var actual = await PostgresStepHarness.UnderCultureAsync(
            string.Empty,
            () => PostgresStepHarness.RenderedAsync(
                renderCase, _fixture.Server.ConnectionString(";Timezone=" + zone)));

        Assert.Equal(renderCase.RenderedUnder(string.Empty), actual);
    }

    [Theory]
    [InlineData("UTC", "01/05/2024 12:00:00")]
    [InlineData("Asia/Tokyo", "01/05/2024 03:00:00")]
    [InlineData("America/New_York", "01/05/2024 17:00:00")]
    public async Task TimestampTz_WithoutOffset_IsInterpretedInTheSessionZone_ThenRenderedAsUtc(string zone, string expected)
    {
        // The session zone matters to the SERVER, which resolves an offset-less literal in it; what the
        // step then compares is the resulting UTC instant. So "12:00 local" differs per session zone.
        var step = PostgresStepHarness.CompileSingleColumn(
            "tz-literal", "SELECT '2024-01-05 12:00:00'::timestamptz AS v", "v");

        var outcome = await PostgresStepHarness.UnderCultureAsync(
            string.Empty,
            () => step.RunAsync(
                _fixture.Server.ConnectionString(";Timezone=" + zone),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = expected }));

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/New_York")]
    public async Task Timestamp_WithoutZone_IsIndependentOfTheSessionZone(string zone)
    {
        var step = PostgresStepHarness.CompileSingleColumn(
            "tz-timestamp", "SELECT '2024-01-05 12:00:00'::timestamp AS v", "v");

        var outcome = await PostgresStepHarness.UnderCultureAsync(
            string.Empty,
            () => step.RunAsync(
                _fixture.Server.ConnectionString(";Timezone=" + zone),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = "01/05/2024 12:00:00" }));

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Theory]
    [InlineData("UTC", "2024-01-05", "01/02/0001 13:45:30 +00:00")]
    [InlineData("Asia/Tokyo", "2024-01-05", "01/02/0001 13:45:30 +09:00")]
    [InlineData("America/New_York", "2024-01-05", "01/02/0001 13:45:30 -05:00")]
    [InlineData("America/New_York", "2024-07-05", "01/02/0001 13:45:30 -04:00")]
    public async Task TimeTz_WithADateButNoOffset_TakesTheSessionZonesOffsetForThatDate(string zone, string date, string expected)
    {
        // Unlike timestamptz, a timetz keeps its offset, and a literal with no offset takes the session
        // zone's offset for the date in the literal. The date is FIXED in every row: PostgreSQL gives
        // a literal with no date at all the offset in force on the CURRENT date, so a test of that form
        // would change its answer with daylight saving. The two New York rows (January -05:00, July
        // -04:00) pin that the offset follows the date.
        var step = PostgresStepHarness.CompileSingleColumn(
            "tz-timetz", "SELECT '" + date + " 13:45:30'::timetz AS v", "v");

        var outcome = await PostgresStepHarness.UnderCultureAsync(
            string.Empty,
            () => step.RunAsync(
                _fixture.Server.ConnectionString(";Timezone=" + zone),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = expected }));

        Assert.True(
            outcome.Verdict == Verdict.Pass,
            $"{zone} on {date}: expected '{expected}'; observed {outcome.Verdict}: {outcome.Observation}");
    }
}
