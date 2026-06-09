// Non-docker tests for S05-A-02: SeedApplier type-dispatch + the broker / document
// deferred seams.  None of these need a database — the document and publish paths
// read+hash a fixture and record through a sink (no store/broker), and the failure
// paths (mismatch, missing fixture) are detected before any I/O to a live resource.
//
// The seams are proven "wired" by recording test-doubles: the test asserts the sink
// captured the dependency, topic/collection and the payload/document CONTENT HASH —
// the same hash the reproducibility envelope (S05-B-03) will record.  No actual
// publish or document-store write happens in M2 (Kafka = Sprint 6; document store =
// later); this is the deliberate wired-but-deferred seam.

using System.Security.Cryptography;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Orchestration;
using Xunit;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Non-docker tests for <see cref="SeedApplier.ApplyAsync"/> type-dispatch and the
/// A-02 deferred seams (<see cref="IBrokerWarmupSink"/> / <see cref="IDocumentSeedSink"/>).
/// </summary>
public sealed class SeedApplierDispatchTests
{
    private const string DummyConnString =
        "Host=localhost;Port=1;Database=db;Username=u;Password=p";

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-dispatch-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ExpectedHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ── Document fixture processed successfully via the seam (no database) ────

    [Fact]
    public async Task ApplyAsync_DocumentFixture_ProcessedAndRecorded_WithoutDatabase()
    {
        var dir = NewTempDir();
        try
        {
            // Arrange — a document-store dependency + a JSON fixture on disk.
            var bytes = """{ "sku": "A1", "name": "Widget" }"""u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "products.json"), bytes);

            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["catalog-store"] = new DependencySeed(
                    Sql: null,
                    Documents: new List<DocumentSeed> { new("products", "products.json") }),
            });

            var documentSink = new RecordingDocumentSeedSink();

            // Act — no discovered connection string is needed for the document path;
            // "seeds successfully" = read + hashed + recorded via the seam.
            await SeedApplier.ApplyAsync(
                seed,
                discoveredServices: new Dictionary<string, object>(),
                dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["catalog-store"] = "mongodb",
                },
                seedBaseDirectory: dir,
                brokerSink: null,
                documentSink: documentSink,
                ct: CancellationToken.None);

            // Assert — the seam captured the document with the expected content hash.
            var recorded = Assert.Single(documentSink.Recorded);
            Assert.Equal("catalog-store", recorded.DependencyName);
            Assert.Equal("products", recorded.Collection);
            Assert.Equal(ExpectedHash(bytes), recorded.DocumentContentHash);
            Assert.Contains("products.json", recorded.DocumentPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Broker publish recorded via the warm-up seam (not published) ─────────

    [Fact]
    public async Task ApplyAsync_BrokerPublish_RecordedViaWarmupSeam_NotPublished()
    {
        var dir = NewTempDir();
        try
        {
            // Arrange — a kafka dependency + a payload fixture on disk.
            var bytes = """{ "snapshot": true }"""u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "catalog.json"), bytes);

            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySeed(
                    Sql: null,
                    Publish: new List<PublishSeed> { new("catalog.snapshot", "catalog.json") }),
            });

            var brokerSink = new RecordingBrokerWarmupSink();

            // Act — the publish path records intent + content hash; it does NOT publish
            // (no broker is dialled; the Kafka provider in Sprint 6 will perform it).
            await SeedApplier.ApplyAsync(
                seed,
                discoveredServices: new Dictionary<string, object>(),
                dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["events"] = "kafka",
                },
                seedBaseDirectory: dir,
                brokerSink: brokerSink,
                documentSink: null,
                ct: CancellationToken.None);

            // Assert — the seam captured topic + payload content hash (proving wired).
            var recorded = Assert.Single(brokerSink.Recorded);
            Assert.Equal("events", recorded.DependencyName);
            Assert.Equal("catalog.snapshot", recorded.Topic);
            Assert.Equal(ExpectedHash(bytes), recorded.PayloadContentHash);
            Assert.Contains("catalog.json", recorded.PayloadPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── NIT-1: a seed kind that does not match the dependency type → Provision ─

    [Fact]
    public async Task ApplyAsync_SqlUnderKafkaDependency_ThrowsMismatchProvision()
    {
        var dir = NewTempDir();
        try
        {
            // Arrange — 'sql' declared under a kafka dependency (the NIT-1 case:
            // without dispatch the applier would dial Npgsql with a Kafka conn string).
            await File.WriteAllTextAsync(Path.Combine(dir, "a.sql"), "SELECT 1;");
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySeed(Sql: new List<string> { "a.sql" }),
            });

            // Act + Assert
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object> { ["events"] = DummyConnString },
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["events"] = "kafka",
                    },
                    seedBaseDirectory: dir,
                    brokerSink: null,
                    documentSink: null,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            // Names the dependency, the seed kind, and the declared type.
            Assert.Contains("events", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("sql", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("kafka", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Equal("events", ex.Info.ResourceName);

            // Environment error, never Fail.
            var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
            Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
            Assert.NotEqual(Verdict.Fail, evt.Verdict);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_PublishUnderPostgresDependency_ThrowsMismatchProvision()
    {
        var dir = NewTempDir();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "catalog.json"), "{}"u8.ToArray());
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["orders-db"] = new DependencySeed(
                    Sql: null,
                    Publish: new List<PublishSeed> { new("t", "catalog.json") }),
            });

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object> { ["orders-db"] = DummyConnString },
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["orders-db"] = "postgres",
                    },
                    seedBaseDirectory: dir,
                    brokerSink: null,
                    documentSink: null,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains("publish", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("postgres", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Unknown dependency type that carries a seed → Provision ──────────────

    [Fact]
    public async Task ApplyAsync_DocumentsUnderUnknownType_ThrowsMismatchProvision()
    {
        var dir = NewTempDir();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "doc.json"), "{}"u8.ToArray());
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["weird"] = new DependencySeed(
                    Sql: null,
                    Documents: new List<DocumentSeed> { new(null, "doc.json") }),
            });

            // 'documents' under a 'redis' dependency — not a document store.
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object>(),
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["weird"] = "redis",
                    },
                    seedBaseDirectory: dir,
                    brokerSink: null,
                    documentSink: null,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains("weird", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("documents", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Missing payload / document fixture → Provision naming the path ────────

    [Fact]
    public async Task ApplyAsync_MissingPublishPayload_ThrowsProvisionNamingPath()
    {
        var dir = NewTempDir();
        try
        {
            var missing = Path.GetFullPath(Path.Combine(dir, "absent-payload.json"));
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySeed(
                    Sql: null,
                    Publish: new List<PublishSeed> { new("t", "absent-payload.json") }),
            });

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object>(),
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["events"] = "kafka",
                    },
                    seedBaseDirectory: dir,
                    brokerSink: null,
                    documentSink: null,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains(missing, ex.Info.Detail, StringComparison.Ordinal);
            Assert.Equal("events", ex.Info.ResourceName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_MissingDocumentFile_ThrowsProvisionNamingPath()
    {
        var dir = NewTempDir();
        try
        {
            var missing = Path.GetFullPath(Path.Combine(dir, "absent-doc.json"));
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["catalog-store"] = new DependencySeed(
                    Sql: null,
                    Documents: new List<DocumentSeed> { new("products", "absent-doc.json") }),
            });

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object>(),
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["catalog-store"] = "mongodb",
                    },
                    seedBaseDirectory: dir,
                    brokerSink: null,
                    documentSink: null,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains(missing, ex.Info.Detail, StringComparison.Ordinal);
            Assert.Equal("catalog-store", ex.Info.ResourceName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Default sink wiring: null sinks fall back to recording defaults ───────

    [Fact]
    public async Task ApplyAsync_NullSinks_FallBackToRecordingDefaults_NoThrow()
    {
        var dir = NewTempDir();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "doc.json"), "{}"u8.ToArray());
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["catalog-store"] = new DependencySeed(
                    Sql: null,
                    Documents: new List<DocumentSeed> { new(null, "doc.json") }),
            });

            // Act — null documentSink ⇒ the applier uses its recording default and the
            // document is processed without error (proves the default seam is wired).
            await SeedApplier.ApplyAsync(
                seed,
                discoveredServices: new Dictionary<string, object>(),
                dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["catalog-store"] = "mongodb",
                },
                seedBaseDirectory: dir,
                brokerSink: null,
                documentSink: null,
                ct: CancellationToken.None);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
