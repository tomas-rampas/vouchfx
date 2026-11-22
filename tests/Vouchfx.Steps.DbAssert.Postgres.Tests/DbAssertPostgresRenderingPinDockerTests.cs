// The real-server twin of DbAssertPostgresRenderingPinTests: the same value matrix, the same
// literals, run against a PostgreSQL container (the image the engine's postgres dependency uses), plus
// the session-time-zone pins that only a real server can answer.
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
    [InlineData("UTC", "01/02/0001 13:45:30 +00:00")]
    [InlineData("Asia/Tokyo", "01/02/0001 13:45:30 +09:00")]
    [InlineData("America/New_York", "01/02/0001 13:45:30 -05:00")]
    public async Task TimeTz_WithoutOffset_TakesItsOffsetFromTheSessionZone(string zone, string expected)
    {
        // Unlike timestamptz, a timetz keeps its offset, and an offset-less literal takes the session's
        // (January offsets here, which is why New York is -05:00).
        var step = PostgresStepHarness.CompileSingleColumn(
            "tz-timetz", "SELECT '13:45:30'::timetz AS v", "v");

        var outcome = await PostgresStepHarness.UnderCultureAsync(
            string.Empty,
            () => step.RunAsync(
                _fixture.Server.ConnectionString(";Timezone=" + zone),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = expected }));

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }
}
