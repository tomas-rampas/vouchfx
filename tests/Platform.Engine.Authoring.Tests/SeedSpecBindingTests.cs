// Tests for S05-A-01: YamlDocumentParser binds environment.seed into a typed
// SeedSpec / DependencySeed (docs/02 §3.2.2).  Written RED-first against the
// public Parse contract.

using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Model;
using Xunit;

namespace Platform.Engine.Authoring.Tests;

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
}
