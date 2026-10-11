using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class SeedSafetyTests
{
    [Fact]
    public void FailureDetails_UseTypeAndValidatedCode_WithoutRawMessage()
    {
        var canary = Guid.NewGuid().ToString("n");
        Assert.Equal("IOException", SeedFailureDetails.Describe(new IOException(canary)));
        Assert.Equal("PostgresException (SQLSTATE 42P01)",
            SeedFailureDetails.Describe(new Npgsql.PostgresException(canary, "ERROR", "ERROR", "42P01")));
        Assert.Equal("PostgresException",
            SeedFailureDetails.Describe(new Npgsql.PostgresException(canary, "ERROR", "ERROR", canary)));
    }

    [Theory]
    [InlineData("postgres", "rooted-inside")]
    [InlineData("sqlserver", "rooted-inside")]
    [InlineData("mysql", "rooted-inside")]
    [InlineData("postgres", "rooted-outside")]
    [InlineData("sqlserver", "rooted-outside")]
    [InlineData("mysql", "rooted-outside")]
    [InlineData("postgres", "escape-present")]
    [InlineData("sqlserver", "escape-present")]
    [InlineData("mysql", "escape-present")]
    [InlineData("postgres", "escape-absent")]
    [InlineData("sqlserver", "escape-absent")]
    [InlineData("mysql", "escape-absent")]
    [InlineData("postgres", "prefix-sibling")]
    [InlineData("sqlserver", "prefix-sibling")]
    [InlineData("mysql", "prefix-sibling")]
    [InlineData("postgres", "case-sibling")]
    [InlineData("sqlserver", "case-sibling")]
    [InlineData("mysql", "case-sibling")]
    public async Task ApplyAsync_UncontainedPath_RefusedBeforeExistenceReadAndDriver(string kind, string pathKind)
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-seed-safety-" + Guid.NewGuid().ToString("n"));
        var baseDir = Path.Combine(root, "suite");
        Directory.CreateDirectory(baseDir);
        try
        {
            var outside = Path.Combine(root, "external.sql");
            await File.WriteAllTextAsync(outside, Guid.NewGuid().ToString("n"));
            await File.WriteAllTextAsync(Path.Combine(baseDir, "inside.sql"), "SELECT 1;");
            Directory.CreateDirectory(Path.Combine(root, "suite-other"));
            await File.WriteAllTextAsync(Path.Combine(root, "suite-other", "external.sql"), "SELECT 1;");
            var declared = pathKind switch
            {
                "rooted-inside" => Path.Combine(baseDir, "inside.sql"),
                "rooted-outside" => outside,
                "escape-present" => "../external.sql",
                "escape-absent" => "../absent.sql",
                "prefix-sibling" => "../suite-other/external.sql",
                "case-sibling" => "../SUITE/external.sql",
                _ => throw new ArgumentOutOfRangeException(nameof(pathKind)),
            };

            // Exclusive lock prevents reading the existing external fixture. The connection
            // string is deliberately unparseable: touching a driver would fail differently.
            using var locked = File.Open(outside, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var error = await Assert.ThrowsAsync<OrchestrationException>(() => ApplyAsync(
                kind, declared, baseDir, "not-a-connection-string", CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, error.Info.Kind);
            Assert.Equal("orders-db", error.Info.ResourceName);
            Assert.Contains(declared, error.Info.Detail, StringComparison.Ordinal);
            Assert.True(
                error.Info.Detail?.Contains(pathKind.StartsWith("rooted", StringComparison.Ordinal)
                    ? "relative to the suite directory" : "outside the suite directory", StringComparison.Ordinal) == true,
                "The fixture must be refused by containment before checking existence or constructing a driver.");
            Assert.Null(error.InnerException);
            if (!pathKind.StartsWith("rooted", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(root, error.Info.Detail, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task ApplyAsync_CallerCancellation_Propagates(string kind)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-cancel-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(baseDir, "reference.sql"), "SELECT 1;");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var connectionString = kind switch
            {
                "postgres" => "Host=127.0.0.1;Port=1;Database=db;Username=u;Password=p;Timeout=1",
                "sqlserver" => "Server=127.0.0.1,1;Database=db;User Id=u;Password=p;Connect Timeout=1;TrustServerCertificate=true",
                "mysql" => "Server=127.0.0.1;Port=1;Database=db;User Id=u;Password=p;Connection Timeout=1",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyAsync(
                kind, "reference.sql", baseDir, connectionString, cancellation.Token));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static Task ApplyAsync(string kind, string declared, string baseDir, string connectionString, CancellationToken ct) =>
        SeedApplier.ApplyAsync(
            new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["orders-db"] = new DependencySeed(new[] { declared }),
            }),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["orders-db"] = connectionString },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["orders-db"] = kind },
            baseDir,
            pathDisclosures: null,
            ct);
}
