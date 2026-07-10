// Tests for S05-A-01: YamlDocumentParser binds environment.seed into a typed
// SeedSpec / DependencySeed (docs/02 §3.2.2).  Written RED-first against the
// public Parse contract.

using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

/// <summary>
/// Verifies that <see cref="YamlDocumentParser.Parse"/> binds the
/// <c>environment.seed</c> block to a strongly-typed <see cref="SeedSpec"/>
/// (S05-A-01).
/// </summary>
public sealed class SeedSpecBindingTests
{
    [Fact]
    public void Parse_SeedWithSqlSequence_BindsToSeedSpec()
    {
        // Arrange — an environment with a seed block naming one dependency and
        // two SQL fixture files, in order.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
              seed:
                orders-db:
                  sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — seed bound to a SeedSpec with the expected dependency + ordered files.
        Assert.NotNull(doc.Environment);
        Assert.NotNull(doc.Environment!.Seed);

        var seed = doc.Environment.Seed!;
        Assert.True(seed.Dependencies.ContainsKey("orders-db"));

        var depSeed = seed.Dependencies["orders-db"];
        Assert.NotNull(depSeed.Sql);
        Assert.Equal(2, depSeed.Sql!.Count);
        Assert.Equal("fixtures/a.sql", depSeed.Sql[0]);
        Assert.Equal("fixtures/b.sql", depSeed.Sql[1]);
    }

    [Fact]
    public void Parse_NoSeedBlock_SeedIsNull()
    {
        // Arrange — an environment with no seed block.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — seed is null (absent block).
        Assert.NotNull(doc.Environment);
        Assert.Null(doc.Environment!.Seed);
    }

    [Fact]
    public void Parse_MultipleDependencies_BindsEachIndependently()
    {
        // Arrange — two seeded dependencies, one with a single file.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                catalog-db:
                  type: postgres
              seed:
                orders-db:
                  sql: [ "orders.sql" ]
                catalog-db:
                  sql: [ "catalog-a.sql", "catalog-b.sql" ]
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var seed = doc.Environment!.Seed!;
        Assert.Equal(2, seed.Dependencies.Count);

        var orders = seed.Dependencies["orders-db"].Sql!;
        Assert.Equal("orders.sql", Assert.Single(orders));

        var catalog = seed.Dependencies["catalog-db"].Sql!;
        Assert.Equal(2, catalog.Count);
        Assert.Equal("catalog-a.sql", catalog[0]);
        Assert.Equal("catalog-b.sql", catalog[1]);
    }

    [Fact]
    public void Parse_SeedSqlNotASequence_ThrowsParseError()
    {
        // Arrange — 'sql' is a scalar, not a sequence (malformed shape).
        const string yaml = """
            environment:
              seed:
                orders-db:
                  sql: "fixtures/a.sql"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert — malformed shape surfaces as a parse error naming the dependency.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("orders-db", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SeedDependencyValueNotMapping_ThrowsParseError()
    {
        // Arrange — a dependency value is a bare scalar file path, not a
        // '{ sql: [...] }' mapping (malformed shape).  Silently dropping it would
        // later surface as a misattributed assertion Fail (§12.1), so the parser
        // must reject it.
        const string yaml = """
            environment:
              seed:
                orders-db: "fixtures/a.sql"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert — malformed shape surfaces as a parse error naming the dependency.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("orders-db", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SeedSqlItemNotScalar_ThrowsParseError()
    {
        // Arrange — a 'sql' item is itself a mapping, not a scalar file path.
        const string yaml = """
            environment:
              seed:
                orders-db:
                  sql:
                    - file: a.sql
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("orders-db", ex.Message, StringComparison.Ordinal);
    }

    // ── A-02: broker warm-up publish binding ─────────────────────────────────

    [Fact]
    public void Parse_SeedWithPublish_BindsTopicAndPayloadFrom()
    {
        // Arrange — a broker dependency with a single warm-up publish.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
              seed:
                events:
                  publish:
                    - topic: catalog.snapshot
                      payload: { from: "fixtures/catalog.json" }
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — publish bound; sql/documents remain null.
        var depSeed = doc.Environment!.Seed!.Dependencies["events"];
        Assert.Null(depSeed.Sql);
        Assert.Null(depSeed.Documents);
        Assert.NotNull(depSeed.Publish);

        var publish = Assert.Single(depSeed.Publish!);
        Assert.Equal("catalog.snapshot", publish.Topic);
        Assert.Equal("fixtures/catalog.json", publish.PayloadFrom);
    }

    [Fact]
    public void Parse_SeedPublishMissingTopic_ThrowsParseError()
    {
        // Arrange — a publish item with no 'topic' scalar.
        const string yaml = """
            environment:
              seed:
                events:
                  publish:
                    - payload: { from: "fixtures/catalog.json" }
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert — names the dependency and the missing 'topic'.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("events", ex.Message, StringComparison.Ordinal);
        Assert.Contains("topic", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SeedPublishMissingPayloadFrom_ThrowsParseError()
    {
        // Arrange — a publish item whose 'payload' mapping has no 'from'.
        const string yaml = """
            environment:
              seed:
                events:
                  publish:
                    - topic: catalog.snapshot
                      payload: {}
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert — names the dependency and the missing 'from'.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("events", ex.Message, StringComparison.Ordinal);
        Assert.Contains("from", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SeedPublishMissingPayloadMapping_ThrowsParseError()
    {
        // Arrange — a publish item with a 'topic' but no 'payload' mapping at all.
        const string yaml = """
            environment:
              seed:
                events:
                  publish:
                    - topic: catalog.snapshot
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert — names the dependency and the missing 'payload'.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("events", ex.Message, StringComparison.Ordinal);
        Assert.Contains("payload", ex.Message, StringComparison.Ordinal);
    }

    // ── A-02: document fixture binding ───────────────────────────────────────

    [Fact]
    public void Parse_SeedWithDocuments_BindsCollectionAndFrom()
    {
        // Arrange — a document-store dependency with a single fixture (with collection).
        const string yaml = """
            environment:
              dependencies:
                catalog-store:
                  type: mongodb
              seed:
                catalog-store:
                  documents:
                    - collection: products
                      from: "fixtures/products.json"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — documents bound; sql/publish remain null.
        var depSeed = doc.Environment!.Seed!.Dependencies["catalog-store"];
        Assert.Null(depSeed.Sql);
        Assert.Null(depSeed.Publish);
        Assert.NotNull(depSeed.Documents);

        var document = Assert.Single(depSeed.Documents!);
        Assert.Equal("products", document.Collection);
        Assert.Equal("fixtures/products.json", document.From);
    }

    [Fact]
    public void Parse_SeedDocumentsWithoutCollection_CollectionIsNull()
    {
        // Arrange — 'collection' is optional.
        const string yaml = """
            environment:
              seed:
                catalog-store:
                  documents:
                    - from: "fixtures/products.json"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — bound with a null collection.
        var document = Assert.Single(doc.Environment!.Seed!.Dependencies["catalog-store"].Documents!);
        Assert.Null(document.Collection);
        Assert.Equal("fixtures/products.json", document.From);
    }

    [Fact]
    public void Parse_SeedDocumentsMissingFrom_ThrowsParseError()
    {
        // Arrange — a document item with no required 'from' file path.
        const string yaml = """
            environment:
              seed:
                catalog-store:
                  documents:
                    - collection: products
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert — names the dependency and the missing 'from'.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("catalog-store", ex.Message, StringComparison.Ordinal);
        Assert.Contains("from", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SeedPublishNotASequence_ThrowsParseError()
    {
        // Arrange — 'publish' is a scalar, not a sequence (malformed shape).
        const string yaml = """
            environment:
              seed:
                events:
                  publish: "not-a-sequence"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("events", ex.Message, StringComparison.Ordinal);
        Assert.Contains("publish", ex.Message, StringComparison.Ordinal);
    }

    // ── Malformed seed dependency KEY rejection (sibling of ParseCaptureMap) ──

    [Fact]
    public void Parse_SeedDependencyNonScalarKey_ThrowsRatherThanSilentlySkipping()
    {
        // Arrange — a seed block whose dependency KEY is a YAML complex (sequence)
        // key rather than a scalar logical dependency name.  The parser must REJECT
        // this per ParseSeed's own contract: a silently-dropped seed dependency
        // leaves a fixture unloaded, so a later step asserts against unseeded data
        // and surfaces as a misattributed assertion Fail / EnvironmentError (§12.1) —
        // the exact confusion seeding prevents.  Mirrors the value-side rejection
        // and the capture-key sibling Parse_StepCapture_NonScalarKey_*.  The rest of
        // the YAML is well-formed so the seed KEY is the ONLY parse error.
        const string yaml = """
            environment:
              services:
                api:
                  image: "example/api:latest"
              seed:
                ? [a, b]
                : { sql: [ "fixtures/x.sql" ] }
            steps:
              - id: s1
                type: http.rest
                target: api
            """;

        // Act + Assert — the malformed key must surface as a YamlParseException.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        // The message must name the requirement (a scalar dependency name).
        Assert.Contains("scalar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seed", ex.Message, StringComparison.OrdinalIgnoreCase);
        // 1-based position is derived from the offending key node (mirrors siblings).
        Assert.True(ex.Line > 0, "Line should be populated from the offending key node.");
        Assert.True(ex.Column > 0, "Column should be populated from the offending key node.");
    }

    [Fact]
    public void Parse_SeedDependencyScalarKey_StillParsesUnchanged()
    {
        // Arrange — back-compat: a normal scalar seed-dependency key must keep
        // working exactly as before the malformed-key rejection was added.
        const string yaml = """
            environment:
              seed:
                orders-db:
                  sql: [ "fixtures/a.sql" ]
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — the scalar-keyed dependency binds unchanged.
        var seed = doc.Environment!.Seed!;
        Assert.True(seed.Dependencies.ContainsKey("orders-db"));
        Assert.Equal("fixtures/a.sql", Assert.Single(seed.Dependencies["orders-db"].Sql!));
    }
}
