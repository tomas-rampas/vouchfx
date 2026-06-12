// Tests for S03-B-01: YamlDocumentParser — YAML deserialisation to a typed document model.
// Written RED-first against the public contract before the implementation existed.

using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Model;
using Platform.Sdk;
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

        // Assert — bare-scalar form is back-compatible: it defaults to JSONPath
        // and preserves the expression text byte-for-byte (S07-B-01a).
        var step = doc.Steps[0];
        Assert.NotNull(step.Capture);
        Assert.Equal(CaptureFormat.JsonPath, step.Capture!["newUserId"].Format);
        Assert.Equal("$.id", step.Capture["newUserId"].Expression);
        Assert.Equal(CaptureFormat.JsonPath, step.Capture["planName"].Format);
        Assert.Equal("$.plan", step.Capture["planName"].Expression);
    }

    // -------------------------------------------------------------------------
    // S07-B-01a — capture format generalisation (JSONPath alongside XPath)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_StepCapture_ExplicitJsonPathMapping_IsJsonPath()
    {
        // Arrange — explicit single-key { jsonpath: … } form.
        const string yaml = """
            steps:
              - id: create-user
                type: http.rest
                capture:
                  newUserId:
                    jsonpath: "$.id"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var step = doc.Steps[0];
        Assert.NotNull(step.Capture);
        Assert.Equal(CaptureFormat.JsonPath, step.Capture!["newUserId"].Format);
        Assert.Equal("$.id", step.Capture["newUserId"].Expression);
    }

    [Fact]
    public void Parse_StepCapture_ExplicitXPathMapping_IsXPath()
    {
        // Arrange — explicit single-key { xpath: … } form.
        const string yaml = """
            steps:
              - id: read-soap
                type: http.rest
                capture:
                  orderId:
                    xpath: "//order/id"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var step = doc.Steps[0];
        Assert.NotNull(step.Capture);
        Assert.Equal(CaptureFormat.XPath, step.Capture!["orderId"].Format);
        Assert.Equal("//order/id", step.Capture["orderId"].Expression);
    }

    [Fact]
    public void Parse_StepCapture_MixedBareAndMappingForms_AreBothBound()
    {
        // Arrange — a bare scalar and an explicit XPath mapping side by side.
        const string yaml = """
            steps:
              - id: mixed
                type: http.rest
                capture:
                  fromJson: "$.id"
                  fromXml:
                    xpath: "//id"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var step = doc.Steps[0];
        Assert.NotNull(step.Capture);
        Assert.Equal(CaptureFormat.JsonPath, step.Capture!["fromJson"].Format);
        Assert.Equal("$.id", step.Capture["fromJson"].Expression);
        Assert.Equal(CaptureFormat.XPath, step.Capture["fromXml"].Format);
        Assert.Equal("//id", step.Capture["fromXml"].Expression);
    }

    [Fact]
    public void Parse_StepCapture_EmptyMapping_ThrowsWithClearMessage()
    {
        // Arrange — a mapping entry with neither 'jsonpath' nor 'xpath'.
        const string yaml = """
            steps:
              - id: bad
                type: http.rest
                capture:
                  x: {}
            """;

        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("capture", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'x'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("jsonpath", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xpath", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StepCapture_BothKeys_ThrowsWithClearMessage()
    {
        // Arrange — a mapping entry declaring both 'jsonpath' and 'xpath'.
        const string yaml = """
            steps:
              - id: bad
                type: http.rest
                capture:
                  x:
                    jsonpath: "$.id"
                    xpath: "//id"
            """;

        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("capture", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'x'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StepCapture_UnknownKey_ThrowsWithClearMessage()
    {
        // Arrange — a mapping entry with an unrecognised key.
        const string yaml = """
            steps:
              - id: bad
                type: http.rest
                capture:
                  x:
                    regex: "id=(.*)"
            """;

        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("capture", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'x'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("regex", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StepCapture_MappingValueNotScalar_ThrowsWithClearMessage()
    {
        // Arrange — the 'jsonpath' value is itself a mapping, not a scalar.
        const string yaml = """
            steps:
              - id: bad
                type: http.rest
                capture:
                  x:
                    jsonpath:
                      nested: oops
            """;

        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("capture", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'x'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_StepCapture_NonScalarKey_ThrowsRatherThanSilentlySkipping()
    {
        // Arrange — a capture block whose KEY is a YAML complex (sequence) key
        // rather than a scalar variable name.  The parser must REJECT this per its
        // own contract (a silently-dropped capture later misattributes an
        // assertion Fail / leaves a {placeholder} unresolved, §12.1), not skip it.
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                capture:
                  ? [a, b]
                  : "$.id"
            """;

        // Act + Assert — the malformed key must surface as a YamlParseException.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("capture", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The message must name the requirement (a scalar variable name).
        Assert.Contains("scalar", ex.Message, StringComparison.OrdinalIgnoreCase);
        // 1-based position is derived from the offending key node (mirrors siblings).
        Assert.True(ex.Line > 0, "Line should be populated from the offending key node.");
        Assert.True(ex.Column > 0, "Column should be populated from the offending key node.");
    }

    [Fact]
    public void Parse_StepCapture_ScalarKey_StillParsesUnchanged()
    {
        // Arrange — back-compat: a normal scalar capture key must keep working
        // exactly as before the malformed-key rejection was added.
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                capture:
                  newUserId: "$.id"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var step = doc.Steps[0];
        Assert.NotNull(step.Capture);
        Assert.Equal(CaptureFormat.JsonPath, step.Capture!["newUserId"].Format);
        Assert.Equal("$.id", step.Capture["newUserId"].Expression);
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

    // -------------------------------------------------------------------------
    // S08-T11 — reject malformed (non-mapping) service / dependency VALUES
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ServiceValueNotMapping_ThrowsRatherThanSilentlyDropping()
    {
        // Arrange — a service whose VALUE is a bare scalar (e.g. an image string)
        // where a '{ image: … }' mapping is expected.  Silently dropping it would
        // leave the system-under-test container unstarted, surfacing later as a
        // misattributed EnvironmentError — the exact §12.1 confusion the parser
        // elsewhere prevents.  The parser must REJECT it.
        const string yaml = """
            environment:
              services:
                api: "myimage:latest"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert — the malformed value must surface as a YamlParseException.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("service", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The message must name the requirement (a mapping such as '{ image: … }').
        Assert.Contains("mapping", ex.Message, StringComparison.OrdinalIgnoreCase);
        // 1-based position is derived from the offending value node (mirrors siblings).
        Assert.True(ex.Line > 0, "Line should be populated from the offending value node.");
        Assert.True(ex.Column > 0, "Column should be populated from the offending value node.");
    }

    [Fact]
    public void Parse_DependencyValueNotMapping_ThrowsRatherThanSilentlyDropping()
    {
        // Arrange — a dependency whose VALUE is a bare scalar where a
        // '{ type: … }' mapping is expected.  Silently dropping it would leave a
        // managed Aspire resource unprovisioned, surfacing later as a misattributed
        // EnvironmentError (§12.1).  The parser must REJECT it.
        const string yaml = """
            environment:
              dependencies:
                orders-db: "postgres"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert — the malformed value must surface as a YamlParseException.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("dependency", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The message must name the requirement (a mapping such as '{ type: … }').
        Assert.Contains("mapping", ex.Message, StringComparison.OrdinalIgnoreCase);
        // 1-based position is derived from the offending value node (mirrors siblings).
        Assert.True(ex.Line > 0, "Line should be populated from the offending value node.");
        Assert.True(ex.Column > 0, "Column should be populated from the offending value node.");
    }

    [Fact]
    public void Parse_ValidServiceMappingAndDependencyMapping_StillParseUnchanged()
    {
        // Arrange — back-compat: a normal '{ image: … }' service and a normal
        // '{ type: … }' dependency must keep parsing exactly as before the
        // malformed-value rejection was added.
        const string yaml = """
            environment:
              services:
                orders-api:
                  image: myorg/orders-api:1.2.3
              dependencies:
                orders-db:
                  type: postgres
                  version: "16"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — the valid service mapping is bound.
        Assert.NotNull(doc.Environment?.Services);
        Assert.True(doc.Environment.Services.ContainsKey("orders-api"));
        Assert.Equal("myorg/orders-api:1.2.3", doc.Environment.Services["orders-api"].Image);

        // Assert — the valid dependency mapping is bound.
        Assert.NotNull(doc.Environment.Dependencies);
        Assert.True(doc.Environment.Dependencies.ContainsKey("orders-db"));
        Assert.Equal("postgres", doc.Environment.Dependencies["orders-db"].Type);
        Assert.Equal("16", doc.Environment.Dependencies["orders-db"].Version);
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
