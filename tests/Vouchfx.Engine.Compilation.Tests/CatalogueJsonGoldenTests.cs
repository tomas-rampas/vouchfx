// Spec A — catalogue JSON wire-shape freeze (REQ-006).
//
// Serialises a FIXED, hand-built StepCatalogueDocument (never the live registry)
// through EngineExport.CatalogueJsonOptions and asserts identity against
// Golden/catalogue-document.v1.json. Evolution within v1 is additive only.
//
// REGENERATION:
//   VOUCHFX_REGEN_CATALOGUE_JSON=1 dotnet test tests/Vouchfx.Engine.Compilation.Tests \
//     --filter "FullyQualifiedName~CatalogueJsonGoldenTests"
//   Rewrites Golden/catalogue-document.v1.json from the freshly-serialised fixture.
//   Review the diff, then commit.

using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class CatalogueJsonGoldenTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_CATALOGUE_JSON";

    private static readonly string[] DbAssertRequired = new[] { "query", "target" };
    private static readonly string[] DbAssertOptional = new[] { "expect" };
    private static readonly string[] HttpRestRequired = new[] { "method", "path", "target" };
    private static readonly string[] HttpRestOptional = new[] { "body", "expect", "headers" };
    private static readonly string[] MqPublishRequired = new[] { "target", "topic" };
    private static readonly string[] MqPublishOptional = new[] { "headers", "payload" };
    private static readonly string[] MqExpectAsbRequired = new[] { "target" };
    private static readonly string[] MqExpectAsbOptional = new[] { "queue", "subscription", "topic" };

    // M2 (gatekeeper, third round): the fixture must PIN the two array-of-arrays shapes
    // BuildCatalogue actually emits, not merely default them to null via the 7-arg ctor —
    // an entry with no root oneOf/anyOf gets an explicit EMPTY array (proving the golden
    // freezes "[]", not merely tolerating an omitted property), and at least one entry per
    // group kind carries a populated group so the nested-array serialisation itself is
    // frozen, not just the outer property's presence.
    private static readonly IReadOnlyList<IReadOnlyList<string>> NoGroups =
        Array.Empty<IReadOnlyList<string>>();
    private static readonly IReadOnlyList<IReadOnlyList<string>> ScriptCsharpExactlyOneOf =
        new IReadOnlyList<string>[] { new[] { "code", "file" } };
    private static readonly IReadOnlyList<IReadOnlyList<string>> MqExpectAsbAtLeastOneOf =
        new IReadOnlyList<string>[] { new[] { "expectPayloadContains", "expectProperties" } };

    // #556 (additive): the fixture pins every shape the four new members take on the wire — a
    // Core entry with all four populated (db-assert.postgres, http.rest); a Core entry whose
    // example carries double quotes (trace-expect.otlp, freezing how the encoder escapes them
    // inside the string); a Community entry (tier stated, no link, no example); an entry from the
    // overload given no Core set (supportedVerifyModes only); and an entry hand-built before the
    // members existed (all four null). The golden therefore freezes the explicit nulls rather
    // than merely tolerating them. Examples are spelled with "\n" escapes, never raw literals,
    // so a CRLF checkout cannot change the bytes under test.
    private static readonly string[] VerifyModes = new[] { "IMMEDIATE", "RETRY" };
    private static readonly string[] TraceExpectRequired = new[] { "match", "receiver" };

    private const string ExampleMetadata =
        "metadata:\n"
        + "  name: scaffolded-suite\n"
        + "  tags: [scaffolded]\n"
        + "  description: Machine-drafted suite skeleton from vouchfx scaffold; review and fill before trust.\n"
        + "\n";

    private const string DbAssertPostgresExample = ExampleMetadata
        + "environment:\n  dependencies:\n    postgres:\n      type: postgres\n\n"
        + "steps:\n  - id: db-assert-postgres\n    type: db-assert.postgres\n"
        + "    expect:\n      rowCount: 0\n    query: SELECT 1 AS scaffold\n    target: postgres\n";

    private const string HttpRestExample = ExampleMetadata
        + "environment:\n  services:\n    app:\n      image: traefik/whoami\n      httpPort: 80\n\n"
        + "steps:\n  - id: http-rest\n    type: http.rest\n    method: GET\n    path: /\n    target: app\n";

    private const string TraceExpectOtlpExample = ExampleMetadata
        + "steps:\n  - id: trace-expect-otlp\n    type: trace-expect.otlp\n"
        + "    match:\n      traceId: \"00000000000000000000000000000001\"\n    receiver: scaffold-receiver\n";

    private static StepCatalogueDocument BuildFixture() => new(
        SchemaVersion: EngineExport.CatalogueSchemaVersion,
        EngineVersion: "1.2.3-golden",
        StepTypes: new[]
        {
            new StepCatalogueEntry(
                Type: "db-assert.postgres",
                Family: "db-assert",
                Provider: "postgres",
                RequiredFields: DbAssertRequired,
                OptionalFields: DbAssertOptional,
                CaptureSupported: true,
                FamilyIntent: "Query a data store and assert properties of the result set or document.",
                ExactlyOneOfGroups: NoGroups,
                AtLeastOneOfGroups: NoGroups)
            {
                Tier = "core",
                SupportedVerifyModes = VerifyModes,
                DocsUrl = "https://vouchfx.io/language-reference/#db-assertpostgres",
                Example = DbAssertPostgresExample,
            },
            new StepCatalogueEntry(
                Type: "http.rest",
                Family: "http",
                Provider: "rest",
                RequiredFields: HttpRestRequired,
                OptionalFields: HttpRestOptional,
                CaptureSupported: true,
                FamilyIntent: "Call HTTP endpoints (REST or SOAP) on services under test and assert responses.",
                ExactlyOneOfGroups: NoGroups,
                AtLeastOneOfGroups: NoGroups)
            {
                Tier = "core",
                SupportedVerifyModes = VerifyModes,
                DocsUrl = "https://vouchfx.io/language-reference/#httprest",
                Example = HttpRestExample,
            },
            new StepCatalogueEntry(
                Type: "mq-publish.kafka",
                Family: "mq-publish",
                Provider: "kafka",
                RequiredFields: MqPublishRequired,
                OptionalFields: MqPublishOptional,
                CaptureSupported: true,
                FamilyIntent: "Publish a message onto a broker to drive the system under test.",
                ExactlyOneOfGroups: NoGroups,
                AtLeastOneOfGroups: NoGroups)
            {
                Tier = "community",
                SupportedVerifyModes = VerifyModes,
            },
            new StepCatalogueEntry(
                Type: "script.csharp",
                Family: "script",
                Provider: "csharp",
                RequiredFields: Array.Empty<string>(),
                OptionalFields: Array.Empty<string>(),
                CaptureSupported: true,
                FamilyIntent: "Run inline or file-backed C# for cases the declarative step types cannot express.",
                ExactlyOneOfGroups: ScriptCsharpExactlyOneOf,
                AtLeastOneOfGroups: NoGroups)
            {
                SupportedVerifyModes = VerifyModes,
            },
            new StepCatalogueEntry(
                Type: "mq-expect.azureservicebus",
                Family: "mq-expect",
                Provider: "azureservicebus",
                RequiredFields: MqExpectAsbRequired,
                OptionalFields: MqExpectAsbOptional,
                CaptureSupported: true,
                FamilyIntent: "Assert that a message matching declared criteria was received from a broker.",
                ExactlyOneOfGroups: NoGroups,
                AtLeastOneOfGroups: MqExpectAsbAtLeastOneOf),
            new StepCatalogueEntry(
                Type: "trace-expect.otlp",
                Family: "trace-expect",
                Provider: "otlp",
                RequiredFields: TraceExpectRequired,
                OptionalFields: Array.Empty<string>(),
                CaptureSupported: true,
                FamilyIntent: "Expect distributed-trace spans (e.g. OTLP) matching declared criteria.",
                ExactlyOneOfGroups: NoGroups,
                AtLeastOneOfGroups: NoGroups)
            {
                Tier = "core",
                SupportedVerifyModes = VerifyModes,
                DocsUrl = "https://vouchfx.io/language-reference/#trace-expectotlp",
                Example = TraceExpectOtlpExample,
            },
        });

    [Fact]
    public void CatalogueDocument_MatchesGolden_ByteForByte()
    {
        var actual = EngineExport.SerializeCatalogue(BuildFixture());

        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Engine.Compilation.Tests", "Golden",
                "catalogue-document.v1.json");
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var golden = ReadGolden("catalogue-document.v1.json");
        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    [Fact]
    public void CatalogueDocument_FrozenPropertyNamesPresent()
    {
        // Additive-only guard: required property names on the fixture serialisation
        // must remain present. Adding a new optional property does not break this test.
        using var doc = JsonDocument.Parse(EngineExport.SerializeCatalogue(BuildFixture()));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.True(root.TryGetProperty("engineVersion", out _));
        Assert.True(root.TryGetProperty("stepTypes", out var stepTypes));
        Assert.Equal(JsonValueKind.Array, stepTypes.ValueKind);

        var first = stepTypes[0];
        Assert.True(first.TryGetProperty("type", out _));
        Assert.True(first.TryGetProperty("family", out _));
        Assert.True(first.TryGetProperty("provider", out _));
        Assert.True(first.TryGetProperty("requiredFields", out _));
        Assert.True(first.TryGetProperty("optionalFields", out _));
        Assert.True(first.TryGetProperty("captureSupported", out _));
        Assert.True(first.TryGetProperty("familyIntent", out _));

        // #556: present on every entry, null or not (JsonIgnoreCondition.Never).
        Assert.All(stepTypes.EnumerateArray(), entry =>
        {
            Assert.True(entry.TryGetProperty("tier", out _));
            Assert.True(entry.TryGetProperty("supportedVerifyModes", out _));
            Assert.True(entry.TryGetProperty("docsUrl", out _));
            Assert.True(entry.TryGetProperty("example", out _));
        });
    }

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').");
    }

    private static string ReadGolden(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", fileName);
        Assert.True(
            File.Exists(path),
            $"Golden file not found at '{path}'. The freeze gate requires "
            + $"Golden/{fileName} to be committed and copied to output.");
        return File.ReadAllText(path);
    }

    private static string Normalise(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
}
