// Tests for S03-B-01: YamlDocumentParser — YAML deserialisation to a typed document model.
// Written RED-first against the public contract before the implementation existed.

using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Model;
using Xunit;

namespace Platform.Engine.Authoring.Tests;

/// <summary>
/// Verifies <see cref="YamlDocumentParser.Parse"/> correctly deserialises
/// <c>.e2e.yaml</c> content into a typed <see cref="E2eDocument"/>.
/// </summary>
public sealed class YamlDocumentParserTests
{
    // -------------------------------------------------------------------------
    // Full document — all four sections present
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_FullDocument_RoundTripsAllFourSections()
    {
        // Arrange — a document with every top-level section populated.
        const string yaml = """
            metadata:
              name: "User registration propagates to billing"
              owner: "payments-team"
              tags: [smoke, billing]
              description: "End-to-end registration flow"

            environment:
              imageRegistry: registry.example.com
              services:
                orders-api:
                  image: myorg/orders-api:1.2.3
              dependencies:
                orders-db:
                  type: postgres
                  version: "16"

            variables:
              tenantId: "acme-corp"
              basePath: "/api/v1"

            steps:
              - id: create-user
                type: http.rest
                description: "Register a new user"
                method: POST
                path: "/api/v1/users"
                expect:
                  status: 201
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — metadata section
        Assert.NotNull(doc.Metadata);
        Assert.Equal("User registration propagates to billing", doc.Metadata.Name);
        Assert.Equal("payments-team", doc.Metadata.Owner);
        Assert.Equal("End-to-end registration flow", doc.Metadata.Description);
        Assert.NotNull(doc.Metadata.Tags);
        Assert.Equal(2, doc.Metadata.Tags.Count);
        Assert.Contains("smoke", doc.Metadata.Tags);
        Assert.Contains("billing", doc.Metadata.Tags);

        // Assert — environment section
        Assert.NotNull(doc.Environment);
        Assert.Equal("registry.example.com", doc.Environment.ImageRegistry);
        Assert.NotNull(doc.Environment.Services);
        Assert.True(doc.Environment.Services.ContainsKey("orders-api"));
        Assert.Equal("myorg/orders-api:1.2.3", doc.Environment.Services["orders-api"].Image);
        Assert.NotNull(doc.Environment.Dependencies);
        Assert.True(doc.Environment.Dependencies.ContainsKey("orders-db"));
        Assert.Equal("postgres", doc.Environment.Dependencies["orders-db"].Type);
        Assert.Equal("16", doc.Environment.Dependencies["orders-db"].Version);

        // Assert — variables section
        Assert.NotNull(doc.Variables);
        Assert.Equal("acme-corp", doc.Variables["tenantId"]);
        Assert.Equal("/api/v1", doc.Variables["basePath"]);

        // Assert — steps section
        Assert.NotNull(doc.Steps);
        Assert.Single(doc.Steps);
        var step = doc.Steps[0];
        Assert.Equal("create-user", step.Id);
        Assert.Equal("http.rest", step.Type);
        Assert.Equal("Register a new user", step.Description);

        // The raw node must be present and contain provider-specific scalars.
        Assert.NotNull(step.RawNode);
        var methodNode = step.RawNode.Children
            .FirstOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode ks && ks.Value == "method")
            .Value as YamlDotNet.RepresentationModel.YamlScalarNode;
        Assert.NotNull(methodNode);
        Assert.Equal("POST", methodNode.Value);

        var pathNode = step.RawNode.Children
            .FirstOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode ks && ks.Value == "path")
            .Value as YamlDotNet.RepresentationModel.YamlScalarNode;
        Assert.NotNull(pathNode);
        Assert.Equal("/api/v1/users", pathNode.Value);
    }

    // -------------------------------------------------------------------------
    // Minimal document — only steps section
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MinimalStepsOnly_IsValid()
    {
        // Arrange — only the mandatory steps section.
        const string yaml = """
            steps:
              - id: ping
                type: http.rest
                path: /health
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — optional sections are null.
        Assert.Null(doc.Metadata);
        Assert.Null(doc.Environment);
        Assert.Null(doc.Variables);

        // Assert — steps has exactly one element.
        Assert.NotNull(doc.Steps);
        Assert.Single(doc.Steps);
        Assert.Equal("ping", doc.Steps[0].Id);
        Assert.Equal("http.rest", doc.Steps[0].Type);
    }

    // -------------------------------------------------------------------------
    // Provider fields survive in RawNode
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_StepRawNode_PreservesProviderFields()
    {
        // Arrange — a step with provider-specific fields that the parser does not
        // know about (target, headers, body) must appear in RawNode for later binding.
        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                headers:
                  Content-Type: application/json
                body:
                  sku: "ABC123"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — step is parsed.
        Assert.Single(doc.Steps);
        var rawNode = doc.Steps[0].RawNode;
        Assert.NotNull(rawNode);

        // Provider field 'target' must survive.
        var targetNode = rawNode.Children
            .FirstOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode ks && ks.Value == "target")
            .Value as YamlDotNet.RepresentationModel.YamlScalarNode;
        Assert.NotNull(targetNode);
        Assert.Equal("orders-api", targetNode.Value);

        // Provider mapping 'headers' must survive.
        var headersNode = rawNode.Children
            .FirstOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode ks && ks.Value == "headers")
            .Value;
        Assert.NotNull(headersNode);
        Assert.IsType<YamlDotNet.RepresentationModel.YamlMappingNode>(headersNode);
    }

    // -------------------------------------------------------------------------
    // Malformed YAML throws YamlParseException
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MalformedYaml_ThrowsYamlParseException()
    {
        // Arrange — indentation inconsistency that YamlDotNet cannot parse.
        const string yaml = """
            steps:
              - id: broken
               type: http.rest
            """;

        // Act & Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.NotNull(ex.Message);
        Assert.NotEmpty(ex.Message);
    }

    // -------------------------------------------------------------------------
    // Line information is retained on the RawNode
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_StepStart_HasLineInfo()
    {
        // Arrange — steps starting on a known line number.
        const string yaml = """
            metadata:
              name: line-info-test

            steps:
              - id: first-step
                type: http.rest
                path: /test
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — the RawNode.Start.Line must be a positive (1-based) value.
        Assert.Single(doc.Steps);
        var line = doc.Steps[0].RawNode.Start.Line;
        Assert.True(line > 0, $"Expected a positive line number but got {line}.");
    }

    // -------------------------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_EmptyInput_ThrowsYamlParseException()
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(string.Empty));
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void Parse_WhitespaceInput_ThrowsYamlParseException()
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse("   \n  \t  "));
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void Parse_NonMappingRoot_ThrowsYamlParseException()
    {
        // A bare scalar at the root is not a valid .e2e.yaml document.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse("just a string"));
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void Parse_StepCapture_IsDeserialised()
    {
        // Arrange — a step with a capture map.
        const string yaml = """
            steps:
              - id: create-user
                type: http.rest
                path: /users
                capture:
                  newUserId: "$.id"
                  planName: "$.plan"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var step = doc.Steps[0];
        Assert.NotNull(step.Capture);
        Assert.Equal("$.id", step.Capture["newUserId"]);
        Assert.Equal("$.plan", step.Capture["planName"]);
    }

    [Fact]
    public void Parse_StepVerifyModeAndTimeout_AreDeserialised()
    {
        // Arrange
        const string yaml = """
            steps:
              - id: expect-event
                type: mq-expect.kafka
                verifyMode: RETRY
                timeout: 30s
                continueOnFailure: true
                target: events
                topic: billing.account.created
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var step = doc.Steps[0];
        Assert.Equal("RETRY", step.VerifyMode);
        Assert.Equal("30s", step.Timeout);
        Assert.True(step.ContinueOnFailure);
    }

    [Fact]
    public void Parse_DependencyExtra_IsRetainedInRawNode()
    {
        // Arrange — a kafka dependency with an extra provider-specific field.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  version: "3.7"
                  schemaRegistry: true
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        Assert.NotNull(doc.Environment?.Dependencies);
        var dep = doc.Environment.Dependencies["events"];
        Assert.Equal("kafka", dep.Type);
        Assert.Equal("3.7", dep.Version);

        // 'schemaRegistry' is an extra field and must survive in Extra.
        Assert.NotNull(dep.Extra);
        var schemaRegistryNode = dep.Extra.Children
            .FirstOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode ks && ks.Value == "schemaRegistry")
            .Value as YamlDotNet.RepresentationModel.YamlScalarNode;
        Assert.NotNull(schemaRegistryNode);
        Assert.Equal("true", schemaRegistryNode.Value);
    }
}
