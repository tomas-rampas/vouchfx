// Tests for S03-B-02: AstBuilder — AST construction and step normalisation.
// Written RED-first against the public contract before the implementation existed.
//
// Registries are built from hand-constructed stub IStepProvider instances so
// tests control the available families precisely without depending on real
// provider assemblies.

using Platform.Engine.Abstractions;
using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Authoring.Model;
using Platform.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Authoring.Tests;

/// <summary>
/// Verifies <see cref="AstBuilder.Build"/> correctly normalises an
/// <see cref="E2eDocument"/> into a <see cref="ScenarioAst"/>.
/// </summary>
public sealed class AstBuilderTests
{
    // =========================================================================
    // Step-type resolution — dotted form
    // =========================================================================

    [Fact]
    public void Build_DottedType_ResolvesProvider()
    {
        // Arrange
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "call-api", type: "http.rest"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        Assert.Single(ast.Steps);
        var node = ast.Steps[0];
        Assert.Equal("http", node.Kind.Family);
        Assert.Equal("rest", node.Kind.Provider);
        Assert.Equal("http.rest", node.CanonicalType);
    }

    // =========================================================================
    // Step-type resolution — bare family alias (single provider)
    // =========================================================================

    [Fact]
    public void Build_BareFamily_SingleProvider_ResolvesAlias()
    {
        // Arrange — registry only has http.rest; bare "http" should resolve to it.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "ping", type: "http"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        var node = ast.Steps[0];
        Assert.Equal("http", node.Kind.Family);
        Assert.Equal("rest", node.Kind.Provider);
        Assert.Equal("http.rest", node.CanonicalType);
    }

    // =========================================================================
    // Step-type resolution — ambiguous bare family
    // =========================================================================

    [Fact]
    public void Build_BareFamily_Ambiguous_Throws()
    {
        // Arrange — two providers in the same family.
        var registry = RegistryWith(
            new StubProvider("mq", "kafka"),
            new StubProvider("mq", "rabbit"));
        var doc = DocWithStep(StepSpecWith(id: "publish", type: "mq"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Step-type resolution — db-assert bare-family guard
    // =========================================================================

    [Fact]
    public void Build_BareDbAssert_Throws_WithClearMessage_NoProviders()
    {
        // Arrange — registry has no db-assert.* provider at all.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "assert-x", type: "db-assert"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("db-assert", ex.Message, StringComparison.Ordinal);
        Assert.Contains("explicit provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_BareDbAssert_Throws_WithClearMessage_WithProviders()
    {
        // Arrange — registry has a db-assert.postgres provider; the bare alias
        // must still be refused (the guard fires before the count-based rules).
        var registry = RegistryWith(new StubProvider("db-assert", "postgres"));
        var doc = DocWithStep(StepSpecWith(id: "assert-y", type: "db-assert"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("db-assert", ex.Message, StringComparison.Ordinal);
        Assert.Contains("explicit provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Step-type resolution — unknown dotted type
    // =========================================================================

    [Fact]
    public void Build_UnknownProvider_Throws()
    {
        // Arrange — http.graphql is not registered.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "gql", type: "http.graphql"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Step-type resolution — unknown bare family
    // =========================================================================

    [Fact]
    public void Build_UnknownBareFamily_Throws()
    {
        // Arrange — "grpc" family has no registrations.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "rpc-call", type: "grpc"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // VerifyMode normalisation
    // =========================================================================

    [Fact]
    public void Build_VerifyMode_DefaultsToImmediate()
    {
        // Arrange — no verifyMode field set.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "s1", type: "http.rest", verifyMode: null));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        Assert.Equal(VerifyMode.Immediate, ast.Steps[0].VerifyMode);
    }

    [Fact]
    public void Build_VerifyMode_Retry_Parsed()
    {
        // Arrange
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "s1", type: "http.rest", verifyMode: "RETRY"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        Assert.Equal(VerifyMode.Retry, ast.Steps[0].VerifyMode);
    }

    [Fact]
    public void Build_VerifyMode_Invalid_Throws()
    {
        // Arrange
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "s1", type: "http.rest", verifyMode: "POLLING"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("POLLING", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // Timeout normalisation
    // =========================================================================

    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("500ms", 500)]
    [InlineData("45", 45_000)]
    [InlineData("2m", 120_000)]
    public void Build_Timeout_Parsed(string raw, int expectedMs)
    {
        // Arrange
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "s1", type: "http.rest", timeout: raw));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        Assert.NotNull(ast.Steps[0].Timeout);
        Assert.Equal(expectedMs, ast.Steps[0].Timeout!.Value.TotalMilliseconds, precision: 0);
    }

    [Fact]
    public void Build_Timeout_Absent_IsNull()
    {
        // Arrange — no timeout field.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "s1", type: "http.rest", timeout: null));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        Assert.Null(ast.Steps[0].Timeout);
    }

    [Fact]
    public void Build_Timeout_Invalid_Throws()
    {
        // Arrange
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "s1", type: "http.rest", timeout: "two-minutes"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("two-minutes", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // Default values — capture empty dict, continueOnFailure false
    // =========================================================================

    [Fact]
    public void Build_Defaults_CaptureEmpty_ContinueFalse()
    {
        // Arrange — step has no capture and no continueOnFailure.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "s1", type: "http.rest"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        var node = ast.Steps[0];
        Assert.NotNull(node.Capture);
        Assert.Empty(node.Capture);
        Assert.False(node.ContinueOnFailure);
    }

    // =========================================================================
    // ScenarioAst — variables default to empty dict
    // =========================================================================

    [Fact]
    public void Build_Variables_DefaultsToEmptyDict()
    {
        // Arrange — document has no variables section.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = new E2eDocument(
            Metadata: null,
            Environment: null,
            Variables: null,
            Steps: new[] { StepSpecWith(id: "s1", type: "http.rest") });

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        Assert.NotNull(ast.Variables);
        Assert.Empty(ast.Variables);
    }

    // =========================================================================
    // AstBuildException carries line info from the YAML node
    // =========================================================================

    [Fact]
    public void AstBuildException_CarriesLineInfo()
    {
        // Arrange — parse real YAML so the node has a genuine Start.Line.
        const string yaml = """
            steps:
              - id: assert-x
                type: db-assert
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var registry = RegistryWith(new StubProvider("http", "rest"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));

        // The step's RawNode.Start.Line must have been threaded through into the exception.
        Assert.True(ex.Line > 0, $"Expected Line > 0 but got {ex.Line}.");

        // The message must also encode the location.
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Integration: real parser → AstBuilder (B-01 → B-02 seam)
    // =========================================================================

    [Fact]
    public void Build_RealYaml_FullDocument_NormalisesAllSteps()
    {
        // Arrange — parse a document that exercises multiple normalisation paths.
        const string yaml = """
            metadata:
              name: integration-seam

            variables:
              baseUrl: "http://localhost:5000"

            steps:
              - id: call-api
                type: http.rest
                verifyMode: RETRY
                timeout: 10s
                continueOnFailure: true
                capture:
                  userId: "$.id"
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var registry = RegistryWith(new StubProvider("http", "rest"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert — document-level
        Assert.NotNull(ast.Metadata);
        Assert.Equal("integration-seam", ast.Metadata.Name);
        Assert.NotNull(ast.Variables);
        Assert.Equal("http://localhost:5000", ast.Variables["baseUrl"]);

        // Assert — step-level normalisation
        Assert.Single(ast.Steps);
        var step = ast.Steps[0];
        Assert.Equal("call-api", step.Id);
        Assert.Equal("http", step.Kind.Family);
        Assert.Equal("rest", step.Kind.Provider);
        Assert.Equal("http.rest", step.CanonicalType);
        Assert.Equal(VerifyMode.Retry, step.VerifyMode);
        Assert.NotNull(step.Timeout);
        Assert.Equal(10_000, step.Timeout!.Value.TotalMilliseconds, precision: 0);
        Assert.True(step.ContinueOnFailure);
        Assert.Single(step.Capture);
        Assert.Equal("$.id", step.Capture["userId"]);
    }

    // =========================================================================
    // AstBuildException — StepId property
    // =========================================================================

    [Fact]
    public void AstBuildException_CarriesStepId()
    {
        // Arrange
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWithStep(StepSpecWith(id: "broken-step", type: "http.graphql"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Equal("broken-step", ex.StepId);
    }

    // =========================================================================
    // Helpers — document and step spec factories
    // =========================================================================

    private static E2eDocument DocWithStep(StepSpec step) =>
        new(Metadata: null, Environment: null, Variables: null, Steps: new[] { step });

    /// <summary>
    /// Constructs a minimal <see cref="StepSpec"/> with a synthetic
    /// <see cref="YamlMappingNode"/> carrying non-zero Start position
    /// information (row 1, column 1).
    /// </summary>
    private static StepSpec StepSpecWith(
        string id,
        string type,
        string? verifyMode = null,
        string? timeout = null,
        bool? continueOnFailure = null,
        IReadOnlyDictionary<string, string>? capture = null)
    {
        // Build a minimal YAML mapping node directly so tests can run without
        // parsing YAML — the Start mark on a freshly constructed node will be
        // at the default position (line 0, col 0) which is acceptable for tests
        // that do not assert on location, while the real-YAML tests parse
        // through YamlDocumentParser and get genuine positions.
        var rawNode = new YamlMappingNode();
        return new StepSpec(id, type, Description: null, capture, verifyMode, timeout, continueOnFailure, rawNode);
    }

    private static StepKindRegistry RegistryWith(params IStepProvider[] providers) =>
        StepKindRegistry.BuildAndFreeze((IReadOnlyCollection<IStepProvider>)providers);
}

// =============================================================================
// File-scoped stub providers — not visible outside this file.
// =============================================================================

/// <summary>
/// Minimal <see cref="IStepProvider"/> stub for use in registry-construction
/// tests.  Avoids depending on real provider assemblies.
/// </summary>
file sealed class StubProvider : IStepProvider
{
    private static readonly IReadOnlyList<string> s_authors = new[] { "test" };

    public StubProvider(string family, string provider)
    {
        Kind = new StepKindId(family, provider);
        Metadata = new ProviderMetadata(
            Version: "1.0.0",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: s_authors);
    }

    public StepKindId Kind { get; }

    public ProviderMetadata Metadata { get; }
}
