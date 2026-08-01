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

    // ── 'publish'/'documents' removed from the v1 language (see SeedSpec.cs) ──
    //
    // Both were wired-but-deferred seams that only read+hashed a referenced
    // fixture and recorded the intent through a sink — never a real broker
    // publish or document-store write — and were removed before general
    // availability. The PARSER stays lenient about an unrecognised key under a
    // seed dependency mapping (mirrors ParseServiceMap: no 'Extra' bucket,
    // every unrecognised key silently vanishes at parse time), exactly as it
    // already does for a typo'd key elsewhere; the JSON Schema is the strict
    // gate that makes a suite still writing 'publish:'/'documents:' fail loudly
    // (root-language-schema.json's $defs/seedDependency now closes with
    // 'additionalProperties: false' recognising only 'sql' — see
    // SeedSchemaTests in Vouchfx.Engine.Compilation.Tests, and
    // Corpus/Rejected/seed-publish-key-removed.e2e.yaml).

    [Fact]
    public void Parse_SeedWithPublishKey_PublishKeyIsSilentlyIgnored_SqlStillBinds()
    {
        // Arrange — 'publish' is no longer a recognised seed kind. The parser does
        // not throw for it (division of responsibility: the schema is the strict
        // gate — see the remarks above); it simply extracts nothing for that key,
        // exactly as ParseServiceMap already does for its own unrecognised keys.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
              seed:
                orders-db:
                  sql: [ "fixtures/a.sql" ]
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

        // Assert — 'sql' still binds; there is no 'Publish'/'Documents' member left
        // on DependencySeed to bind 'publish' into at all.
        var depSeed = doc.Environment!.Seed!.Dependencies["orders-db"];
        Assert.Equal("fixtures/a.sql", Assert.Single(depSeed.Sql!));
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
