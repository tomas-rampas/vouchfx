// Pins whether db-assert.postgres rendering depends on the SESSION time zone or the PROCESS time
// zone (TZ), on Npgsql 8.0.9, and what the connection itself sets. Fake-server half; the session
// zone's effect on a real server is pinned by DbAssertPostgresRenderingPinDockerTests.
//
// Finding: the provider sets no session time zone (the startup message carries only user, database
// and client_encoding); timestamptz is delivered as UTC and rendered from a DateTime of Kind Utc,
// timestamp and timetz as unconverted values, so none of the renderings consults the process zone.
using System.Globalization;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

[Collection(PostgresPinCollectionDefinition.Name)]
public sealed class DbAssertPostgresTimeZonePinTests : IClassFixture<PostgresWireFakeFixture>
{
    private static readonly string[] s_startupKeys = { "client_encoding", "database", "user" };

    private static readonly string[] s_zones = { "UTC", "Asia/Tokyo", "America/New_York" };

    private readonly PostgresWireFakeFixture _fixture;

    public DbAssertPostgresTimeZonePinTests(PostgresWireFakeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Connection_SetsNoSessionTimeZone_OfItsOwn()
    {
        var before = _fixture.Fake.StartupParameters.Count;
        var renderCase = PostgresRenderCases.Find("timestamp with time zone / zero time of day, UTC");

        await PostgresStepHarness.RenderedAsync(renderCase, _fixture.ConnectionString());

        var startup = _fixture.Fake.StartupParameters.Skip(before).Single();
        Assert.Equal(
            s_startupKeys,
            startup.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("UTF8", startup["client_encoding"]);
    }

    [Fact]
    public async Task ConnectionString_Timezone_IsTheOnlyWayTheSessionZoneIsSet()
    {
        var before = _fixture.Fake.StartupParameters.Count;
        var renderCase = PostgresRenderCases.Find("timestamp with time zone / zero time of day, UTC");

        await PostgresStepHarness.RenderedAsync(renderCase, _fixture.ConnectionString(";Timezone=Asia/Tokyo"));

        var startup = _fixture.Fake.StartupParameters.Skip(before).Single();
        Assert.Equal("Asia/Tokyo", startup["TimeZone"]);
    }

    [Theory]
    [InlineData("timestamp with time zone / zero time of day, UTC")]
    [InlineData("timestamp with time zone / fractional seconds, +02")]
    [InlineData("timestamp / fractional seconds")]
    [InlineData("time with time zone / fractional seconds, +02")]
    public async Task RenderedText_DoesNotDependOnTheProcessTimeZone(string caseName)
    {
        var renderCase = PostgresRenderCases.Find(caseName);
        var connection = _fixture.ConnectionString();
        var originalTz = Environment.GetEnvironmentVariable("TZ");
        try
        {
            var rendered = new List<string>();
            var zones = new List<string>();
            foreach (var tz in s_zones)
            {
                Environment.SetEnvironmentVariable("TZ", tz);
                TimeZoneInfo.ClearCachedData();
                zones.Add(TimeZoneInfo.Local.BaseUtcOffset.ToString("c", CultureInfo.InvariantCulture));
                rendered.Add(await PostgresStepHarness.UnderCultureAsync(
                    string.Empty, () => PostgresStepHarness.RenderedAsync(renderCase, connection)));
            }

            // The TZ variable is honoured on Unix only; on Windows the three runs share one zone and
            // the independence below is trivially true there, so only insist the zones moved where they can.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(3, zones.Distinct(StringComparer.Ordinal).Count());
            }

            Assert.Single(rendered.Distinct(StringComparer.Ordinal));
            Assert.Equal(renderCase.RenderedUnder(string.Empty), rendered[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", originalTz);
            TimeZoneInfo.ClearCachedData();
        }
    }
}
