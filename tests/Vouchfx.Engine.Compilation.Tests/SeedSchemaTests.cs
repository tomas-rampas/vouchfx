// Pre-GA schema tightening — close environment.seed, removing the two stub
// kinds (root-language-schema.json).
//
// Before this change, 'seed' was typed only "type": "object" — no shape
// constraint on its dependency entries at all. Three seed kinds existed in
// SeedSpec/DependencySeed: 'sql', 'publish', and 'documents'. 'publish' and
// 'documents' were wired-but-deferred stubs — SeedApplier read the referenced
// fixture, content-hashed it, and recorded the intent through an injectable
// sink (IBrokerWarmupSink / IDocumentSeedSink), but never performed the actual
// broker publish or document-store write. Nothing in the repo used either
// kind. Both were removed from the v1 language surface entirely (see
// SeedSpec.cs's header remarks and CHANGELOG.md) rather than left silently
// inert: a field that does nothing is worse than one that does not exist.
// This class pins the closed shape that remains — 'sql' as an array of
// strings — and proves a suite still writing 'publish:'/'documents:' now
// fails schema validation instead of silently no-opping.
//
// These tests exercise the ROOT schema only (YamlSchemaValidator, no provider
// fragments) — 'environment.seed' constraints live entirely in
// root-language-schema.json. See SchemaAcceptedCorpusTests /
// SchemaRejectedCorpusTests for the corpus-level safety net (via
// DocumentValidator, the composed path an author's suite actually hits) that
// these unit tests complement.
using System.Linq;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Root-schema unit tests for the pre-GA <c>environment.seed</c> closure.
/// </summary>
public sealed class SeedSchemaTests
{
    // ── Accepted shapes ──────────────────────────────────────────────────────

    [Fact]
    public void Seed_SqlArrayOfStrings_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
              seed:
                orders-db:
                  sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// A dependency mapping that names no seed kind at all is a deliberate
    /// no-op (SeedApplier's own dispatcher skips it) — the schema must not
    /// require 'sql' to be present.
    /// </summary>
    [Fact]
    public void Seed_EmptyDependencyMapping_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
              seed:
                orders-db: {}
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    // ── Rejected shapes ──────────────────────────────────────────────────────

    [Fact]
    public void Seed_SqlNotAnArray_IsRejected()
    {
        const string yaml = """
            environment:
              seed:
                orders-db:
                  sql: "fixtures/a.sql"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A 'sql' entry that is a bare scalar (not an array) must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/seed/orders-db/sql" &&
            e.Message.Contains("[type]", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Seed_SqlItemNotAScalar_IsRejected()
    {
        const string yaml = """
            environment:
              seed:
                orders-db:
                  sql:
                    - file: a.sql
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A 'sql' item that is a mapping (not a scalar file path) must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/seed/orders-db/sql/0" &&
            e.Message.Contains("[type]", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Seed_DependencyBareScalarValue_IsRejected()
    {
        const string yaml = """
            environment:
              seed:
                orders-db: "fixtures/a.sql"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A bare-scalar seed dependency value must be rejected — it must be a mapping.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/seed/orders-db" &&
            e.Message.Contains("[type]", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// 'publish' (broker warm-up) was removed from the v1 language — see the
    /// class header remarks. A suite still writing it must fail schema
    /// validation, naming the offending key, rather than silently no-opping
    /// the way the removed wired-but-deferred seam used to.
    /// </summary>
    [Fact]
    public void Seed_PublishKey_IsRejected()
    {
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
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'publish' is no longer a recognised seed kind and must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/seed/events/publish" &&
            e.Message.Contains("[additionalProperties]", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// 'documents' (document-store fixture) was removed from the v1 language —
    /// see the class header remarks. Same closure as 'publish' above.
    /// </summary>
    [Fact]
    public void Seed_DocumentsKey_IsRejected()
    {
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
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'documents' is no longer a recognised seed kind and must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/seed/catalog-store/documents" &&
            e.Message.Contains("[additionalProperties]", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Seed_UnknownKey_IsRejected()
    {
        const string yaml = """
            environment:
              seed:
                orders-db:
                  sql: [ "fixtures/a.sql" ]
                  extra: "not a real field"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An unrecognised seed dependency key must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/seed/orders-db/extra" &&
            e.Message.Contains("[additionalProperties]", System.StringComparison.Ordinal));
    }
}
