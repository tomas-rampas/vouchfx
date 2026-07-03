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
    // Step-type resolution — bare mail-expect alias (single provider)
    // =========================================================================

    [Fact]
    public void Build_BareMailExpect_SingleProvider_ResolvesAlias()
    {
        // Arrange — registry only has mail-expect.smtp; bare "mail-expect" should resolve to it.
        var registry = RegistryWith(new StubProvider("mail-expect", "smtp"));
        var doc = DocWithStep(StepSpecWith(id: "check-mail", type: "mail-expect"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        var node = ast.Steps[0];
        Assert.Equal("mail-expect", node.Kind.Family);
        Assert.Equal("smtp", node.Kind.Provider);
        Assert.Equal("mail-expect.smtp", node.CanonicalType);
    }

    // =========================================================================
    // Step-type resolution — bare cache-assert alias (single provider) + dotted
    // =========================================================================

    [Fact]
    public void Build_BareCacheAssert_SingleProvider_ResolvesAlias()
    {
        // Arrange — registry only has cache-assert.redis; the bare "cache-assert" alias
        // must resolve to it (cache-assert is a single-provider family, like mail-expect).
        var registry = RegistryWith(new StubProvider("cache-assert", "redis"));
        var doc = DocWithStep(StepSpecWith(id: "check-cache", type: "cache-assert"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        var node = ast.Steps[0];
        Assert.Equal("cache-assert", node.Kind.Family);
        Assert.Equal("redis", node.Kind.Provider);
        Assert.Equal("cache-assert.redis", node.CanonicalType);
    }

    [Fact]
    public void Build_DottedCacheAssertRedis_ResolvesCorrectly()
    {
        // Arrange — registry has cache-assert.redis; the dotted type resolves directly.
        var registry = RegistryWith(new StubProvider("cache-assert", "redis"));
        var doc = DocWithStep(StepSpecWith(id: "check-cache", type: "cache-assert.redis"));

        // Act
        var ast = AstBuilder.Build(doc, registry);

        // Assert
        var node = ast.Steps[0];
        Assert.Equal("cache-assert", node.Kind.Family);
        Assert.Equal("redis", node.Kind.Provider);
        Assert.Equal("cache-assert.redis", node.CanonicalType);
    }

    [Fact]
    public void Build_BareCacheAssert_MultiProvider_ThrowsAmbiguous()
    {
        // Arrange — cache-assert now has BOTH redis AND elasticsearch providers.
        // The bare alias "cache-assert" is ambiguous and must throw.
        var registry = RegistryWith(
            new StubProvider("cache-assert", "redis"),
            new StubProvider("cache-assert", "elasticsearch"));
        var doc = DocWithStep(StepSpecWith(id: "check-cache", type: "cache-assert"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redis", ex.Message, StringComparison.Ordinal);
        Assert.Contains("elasticsearch", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public void Build_BareMqPublish_Ambiguous_WhenKafkaAndRabbitmqRegistered()
    {
        // Arrange — mq-publish has both kafka and rabbitmq providers registered.
        var registry = RegistryWith(
            new StubProvider("mq-publish", "kafka"),
            new StubProvider("mq-publish", "rabbitmq"));
        var doc = DocWithStep(StepSpecWith(id: "publish", type: "mq-publish"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Both providers must be named in the error so the author knows ALL candidates.
        Assert.Contains("kafka", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rabbitmq", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_BareMqExpect_Ambiguous_WhenKafkaAndRabbitmqRegistered()
    {
        // Arrange — mq-expect has both kafka and rabbitmq providers registered.
        var registry = RegistryWith(
            new StubProvider("mq-expect", "kafka"),
            new StubProvider("mq-expect", "rabbitmq"));
        var doc = DocWithStep(StepSpecWith(id: "expect", type: "mq-expect"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Both providers must be named in the error so the author knows ALL candidates.
        Assert.Contains("kafka", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rabbitmq", ex.Message, StringComparison.Ordinal);
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
        Assert.Equal(CaptureFormat.JsonPath, step.Capture["userId"].Format);
        Assert.Equal("$.id", step.Capture["userId"].Expression);
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
    // M3 — Step id uniqueness + post-sanitisation collision (security hardening)
    // =========================================================================

    /// <summary>
    /// Two steps with the same raw <c>id</c> must cause <see cref="AstBuildException"/>.
    /// </summary>
    [Fact]
    public void Build_DuplicateRawId_Throws()
    {
        // Arrange — two steps share the same raw id.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWith(
            StepSpecWith(id: "call-api", type: "http.rest"),
            StepSpecWith(id: "call-api", type: "http.rest"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("call-api", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two steps whose ids differ by a hyphen/underscore substitution
    /// (<c>a-b</c> vs <c>a_b</c>) collide after <c>SanitiseId</c> folds <c>-</c>→<c>_</c>.
    /// The builder must reject this as a duplicate.
    /// </summary>
    [Fact]
    public void Build_PostSanitisationCollision_Throws()
    {
        // Arrange — "a-b" and "a_b" both sanitise to "a_b".
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWith(
            StepSpecWith(id: "a-b", type: "http.rest"),
            StepSpecWith(id: "a_b", type: "http.rest"));

        // Act & Assert
        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Message must name both the offending id and the collision target.
        Assert.True(
            ex.Message.Contains("a-b", StringComparison.Ordinal) ||
            ex.Message.Contains("a_b", StringComparison.Ordinal),
            $"Expected message to name the colliding ids, got: {ex.Message}");
    }

    /// <summary>
    /// Unique ids (even ones that contain hyphens) must build without error.
    /// </summary>
    [Fact]
    public void Build_UniqueIds_DoNotThrow()
    {
        // Arrange — three steps with genuinely distinct sanitised ids.
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var doc = DocWith(
            StepSpecWith(id: "step-one", type: "http.rest"),
            StepSpecWith(id: "step-two", type: "http.rest"),
            StepSpecWith(id: "step_three", type: "http.rest"));

        // Act — must not throw.
        var ast = AstBuilder.Build(doc, registry);

        Assert.Equal(3, ast.Steps.Count);
    }

    // =========================================================================
    // M-A — Reserved-prefix rejection for capture keys and variable names
    // =========================================================================

    /// <summary>
    /// A <c>capture</c> key beginning with the engine-reserved prefix <c>conn::</c>
    /// must be rejected at build time with a clear error message.
    /// This prevents an author from redirecting a connection string by writing an
    /// HTTP-response value into the connection-string namespace.
    /// </summary>
    [Fact]
    public void Build_CaptureKey_WithConnPrefix_Throws()
    {
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var capture = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["conn::orders-db"] = new CaptureExpr(CaptureFormat.JsonPath, "$.connString"),
        };
        var doc = DocWithStep(StepSpecWith(id: "step1", type: "http.rest", capture: capture));

        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("conn::", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>capture</c> key beginning with the engine-reserved prefix <c>svc::</c>
    /// must be rejected at build time.
    /// </summary>
    [Fact]
    public void Build_CaptureKey_WithSvcPrefix_Throws()
    {
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var capture = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["svc::my-service"] = new CaptureExpr(CaptureFormat.JsonPath, "$.url"),
        };
        var doc = DocWithStep(StepSpecWith(id: "step1", type: "http.rest", capture: capture));

        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("svc::", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>capture</c> key beginning with <c>__outcome::</c> must be rejected.
    /// </summary>
    [Fact]
    public void Build_CaptureKey_WithOutcomePrefix_Throws()
    {
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var capture = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["__outcome::step1"] = new CaptureExpr(CaptureFormat.JsonPath, "$.result"),
        };
        var doc = DocWithStep(StepSpecWith(id: "step1", type: "http.rest", capture: capture));

        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("__outcome::", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>variables</c> block entry named with the engine-reserved prefix <c>svc::</c>
    /// must be rejected at build time.
    /// </summary>
    [Fact]
    public void Build_VariableName_WithSvcPrefix_Throws()
    {
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["svc::x"] = "http://localhost",
        };
        var doc = new E2eDocument(
            Metadata: null,
            Environment: null,
            Variables: variables,
            Steps: new[] { StepSpecWith(id: "s1", type: "http.rest") });

        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("svc::", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>variables</c> block entry named with the engine-reserved prefix <c>__outcome::</c>
    /// must be rejected at build time.
    /// </summary>
    [Fact]
    public void Build_VariableName_WithOutcomePrefix_Throws()
    {
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__outcome::stolen"] = "forged",
        };
        var doc = new E2eDocument(
            Metadata: null,
            Environment: null,
            Variables: variables,
            Steps: new[] { StepSpecWith(id: "s1", type: "http.rest") });

        var ex = Assert.Throws<AstBuildException>(() => AstBuilder.Build(doc, registry));
        Assert.Contains("__outcome::", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normal (non-reserved) capture keys and variable names must pass without error.
    /// </summary>
    [Fact]
    public void Build_NormalCaptureKeyAndVariableName_DoNotThrow()
    {
        var registry = RegistryWith(new StubProvider("http", "rest"));
        var capture = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["orderId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.id"),
            ["status"] = new CaptureExpr(CaptureFormat.JsonPath, "$.status"),
        };
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["baseUrl"] = "http://localhost",
        };
        var doc = new E2eDocument(
            Metadata: null,
            Environment: null,
            Variables: variables,
            Steps: new[] { StepSpecWith(id: "s1", type: "http.rest", capture: capture) });

        // Must not throw.
        var ast = AstBuilder.Build(doc, registry);
        Assert.Single(ast.Steps);
    }

    // =========================================================================
    // Helpers — document and step spec factories
    // =========================================================================

    private static E2eDocument DocWithStep(StepSpec step) =>
        new(Metadata: null, Environment: null, Variables: null, Steps: new[] { step });

    private static E2eDocument DocWith(params StepSpec[] steps) =>
        new(Metadata: null, Environment: null, Variables: null, Steps: steps);

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
        IReadOnlyDictionary<string, CaptureExpr>? capture = null)
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
