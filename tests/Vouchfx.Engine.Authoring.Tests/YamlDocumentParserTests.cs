// Tests for S03-B-01: YamlDocumentParser — YAML deserialisation to a typed document model.
// Written RED-first against the public contract before the implementation existed.

using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

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

    // -------------------------------------------------------------------------
    // Dependency 'image:' override (private-registry escape hatch)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DependencyImage_ParsesIntoTypedField()
    {
        // Arrange — a dependency naming a private-registry image via 'image:'.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: mongodb
                  image: nexus.corp.example.com/mirror/mongo:7.0
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — 'image' is bound to the typed DependencySpec.Image field.
        var dep = doc.Environment!.Dependencies!["orders-db"];
        Assert.Equal("mongodb", dep.Type);
        Assert.Equal("nexus.corp.example.com/mirror/mongo:7.0", dep.Image);
    }

    [Fact]
    public void Parse_DependencyImage_DoesNotLeakIntoExtra()
    {
        // Arrange — this is the regression that matters most: before DependencySpec
        // gained a typed Image field, 'image:' under a dependency validated fine
        // against the (then-untyped) JSON Schema, was swept into the untyped Extra
        // bucket by BuildExtraNode, and was never read by anything — silently
        // perturbing the environment hash while doing nothing. Once 'image' is a
        // named exclusion in BuildExtraNode, it must no longer appear in Extra.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: mongodb
                  image: nexus.corp.example.com/mirror/mongo:7.0
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — 'image' is the ONLY extra-looking field, so Extra must now be null.
        var dep = doc.Environment!.Dependencies!["orders-db"];
        Assert.Equal("nexus.corp.example.com/mirror/mongo:7.0", dep.Image);
        Assert.Null(dep.Extra);
    }

    [Fact]
    public void Parse_DependencyWithoutImage_ImageIsNullAndBehaviourIsUnchanged()
    {
        // Arrange — a dependency that never mentions 'image:' at all; back-compat
        // with every dependency written before this field existed.
        const string yaml = """
            environment:
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

        // Assert
        var dep = doc.Environment!.Dependencies!["orders-db"];
        Assert.Equal("postgres", dep.Type);
        Assert.Equal("16", dep.Version);
        Assert.Null(dep.Image);
        Assert.Null(dep.Extra);
    }

    [Fact]
    public void Parse_DependencyTypeVersionAndImageTogether_AllThreeLandInTypedFields()
    {
        // Arrange — 'type', 'version', and 'image' all present together; each must
        // land in its own typed field, and none of them should spill into Extra.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  version: "3.7"
                  image: nexus.corp.example.com/mirror/kafka:3.7
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var dep = doc.Environment!.Dependencies!["events"];
        Assert.Equal("kafka", dep.Type);
        Assert.Equal("3.7", dep.Version);
        Assert.Equal("nexus.corp.example.com/mirror/kafka:3.7", dep.Image);
        Assert.Null(dep.Extra);
    }

    [Fact]
    public void Parse_DependencyUnrelatedExtraKeyAlongsideImage_StillLandsInExtra()
    {
        // Arrange — an unrelated provider-specific field (e.g. Kafka's
        // 'schemaRegistry: true') alongside 'image:' proves the Extra bucket was
        // not over-tightened: excluding 'image' must not accidentally exclude (or
        // fail to capture) genuinely unrecognised fields.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  version: "3.7"
                  image: nexus.corp.example.com/mirror/kafka:3.7
                  schemaRegistry: true
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — 'image' is typed, and 'schemaRegistry' is the only survivor in Extra.
        var dep = doc.Environment!.Dependencies!["events"];
        Assert.Equal("nexus.corp.example.com/mirror/kafka:3.7", dep.Image);
        Assert.NotNull(dep.Extra);
        Assert.Single(dep.Extra.Children);
        var schemaRegistryNode = dep.Extra.Children
            .FirstOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode ks && ks.Value == "schemaRegistry")
            .Value as YamlDotNet.RepresentationModel.YamlScalarNode;
        Assert.NotNull(schemaRegistryNode);
        Assert.Equal("true", schemaRegistryNode.Value);
    }

    // -------------------------------------------------------------------------
    // Dependency 'version:'/'image:' plain YAML-null tokens (66aef95-extension fix).
    //
    // The shipped schema descriptions for both fields promise "YAML's explicit null ... parses
    // as null and is treated identically to being absent". GetScalar alone only fulfilled that
    // for a dangling/explicit-empty scalar (Value == ""); a PLAIN '~'/'null'/'Null'/'NULL' was
    // handed through as its own literal text, so 'image: ~' threw deep inside EnvironmentMapper
    // and 'version: ~' silently became a garbage container tag. GetScalarOrPlainNull closes that
    // gap for exactly these two dependency fields, exactly for PLAIN style — a QUOTED value
    // (author explicitly opted out of YAML's null) is always read back literally, unchanged.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("~")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("NULL")]
    public void Parse_DependencyImage_PlainNullToken_ResolvesToNull(string token)
    {
        var yaml = $"""
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: {token}
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Null(doc.Environment!.Dependencies!["orders-db"].Image);
    }

    [Theory]
    [InlineData("~")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("NULL")]
    public void Parse_DependencyVersion_PlainNullToken_ResolvesToNull(string token)
    {
        var yaml = $"""
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  version: {token}
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Null(doc.Environment!.Dependencies!["orders-db"].Version);
    }

    /// <summary>
    /// Hard requirement: an author who explicitly QUOTES '~' means the literal one-character
    /// string, not YAML's null — <see cref="YamlDotNet.RepresentationModel.YamlScalarNode.Style"/>
    /// distinguishes plain from quoted, and only plain style is resolved to null. Covers both
    /// double- and single-quote styles, since both are equally "not plain".
    /// </summary>
    [Theory]
    [InlineData("\"~\"")]
    [InlineData("'~'")]
    public void Parse_DependencyImage_QuotedTildeString_StaysLiteral(string quotedToken)
    {
        var yaml = $"""
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: {quotedToken}
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Equal("~", doc.Environment!.Dependencies!["orders-db"].Image);
    }

    /// <summary>Symmetric quoted-stays-literal requirement for 'version:'.</summary>
    [Theory]
    [InlineData("\"~\"")]
    [InlineData("'~'")]
    public void Parse_DependencyVersion_QuotedTildeString_StaysLiteral(string quotedToken)
    {
        var yaml = $"""
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  version: {quotedToken}
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Equal("~", doc.Environment!.Dependencies!["orders-db"].Version);
    }

    /// <summary>
    /// The four recognised tokens are exact, case-SENSITIVE spellings (YAML 1.2 core schema,
    /// matching the DSL's existing exact-case convention for other vocabulary terms) — a
    /// plausible near-miss like 'NuLL' is not one of them and must be read back as literal text,
    /// not silently swallowed into null.
    /// </summary>
    [Fact]
    public void Parse_DependencyImage_MixedCaseNullToken_IsNotRecognised_StaysLiteral()
    {
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: NuLL
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Equal("NuLL", doc.Environment!.Dependencies!["orders-db"].Image);
    }

    /// <summary>
    /// A Plain-styled scalar can still carry an EXPLICIT tag overriding its type.
    /// 'image: !!str null' forces the value to be read as a string — the author's explicit
    /// '!!str' must win over the fact that its text happens to spell a null token.
    /// GetScalarOrPlainNull requires Tag.IsEmpty (no explicit tag at all) alongside Style and
    /// text, precisely so an explicitly-tagged scalar is never collapsed to null regardless of
    /// what its text says.
    /// </summary>
    [Fact]
    public void Parse_DependencyImage_ExplicitStrTagOnNullText_StaysLiteral()
    {
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: !!str null
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Equal("null", doc.Environment!.Dependencies!["orders-db"].Image);
    }

    /// <summary>
    /// Defensive parser-API detail, not an author-visible gap — confirmed against the actual
    /// schema-validation entry point (see GetScalarOrPlainNull's own remarks): 'image: !!null y'
    /// explicitly tags non-null-looking text with YAML's null tag, which under full YAML 1.2
    /// core-schema tag resolution means the node IS null regardless of its text. This helper does
    /// not attempt that — it only recognises PLAIN, untagged scalars whose TEXT is one of the
    /// five "no real content" spellings — so '!!null y' stays the literal text "y" both before
    /// and after the N-5 tag check. This is pinned only as a parser-API boundary: a document
    /// containing '!!null y' never reaches this method in the shipped pipeline at all —
    /// DocumentValidator.Validate rejects it first with "Encountered an unresolved tag
    /// 'tag:yaml.org,2002:null'" (confirmed empirically), so no real author-authored suite can
    /// ever exercise this path.
    /// </summary>
    [Fact]
    public void Parse_DependencyImage_ExplicitNullTagOnNonNullText_StaysLiteral_KnownAsymmetry()
    {
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: !!null y
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Equal("y", doc.Environment!.Dependencies!["orders-db"].Image);
    }

    /// <summary>
    /// Single-representation-of-absent contract: a dangling 'image:' (no value at all) resolves
    /// to actual <see langword="null"/>, not "". Before this test's own fix, the typed model had
    /// TWO spellings of "absent" for these two fields — "" from a dangling key, alongside null
    /// from a fully-absent key — and a future consumer written as <c>is not null</c> against
    /// that two-spelling shape would silently treat "" as present, which is the exact bug shape
    /// this file's history exists to close. GetScalarOrPlainNull now folds the empty PLAIN scalar
    /// into the same single null representation as the four explicit YAML-null tokens, so every
    /// PLAIN "no real content" spelling collapses to one value — NOT every possible authored
    /// value: a QUOTED 'image: ""' still returns "" verbatim (quoting is the author's deliberate
    /// opt-out; see GetScalarOrPlainNull's own remarks), so this parser still hands out both ""
    /// and null depending on how the author wrote it. EnvironmentMapper's own IsNullOrEmpty
    /// guards are what actually catch that QUOTED "" shape for real authored YAML — they are
    /// LOAD-BEARING, not a defensive fallback that merely covers a hand-constructed
    /// DependencySpec bypassing this parser (see EnvironmentMapper's own guard comment).
    /// </summary>
    [Fact]
    public void Parse_DependencyImage_Dangling_ResolvesToNull_SingleAbsentRepresentation()
    {
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image:
            steps:
              - id: noop
                type: script.csharp
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Null(doc.Environment!.Dependencies!["orders-db"].Image);
    }

    // -------------------------------------------------------------------------
    // SUT configuration surface — service 'env:' mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ServiceEnv_Absent_IsNull()
    {
        // Arrange — a service with no 'env:' block at all.
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        Assert.Null(doc.Environment!.Services!["api"].Env);
    }

    [Fact]
    public void Parse_ServiceEnv_LiteralAndReferenceValues_AreBoundAsRawStrings()
    {
        // Arrange — a mix of a literal value and ${conn:...} reference values, including a
        // bare-integer-looking scalar (the YAML-scalar-coercion gotcha this parser is
        // elsewhere careful about: '8080' must survive as the STRING "8080", not be coerced).
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  httpPort: 8080
                  env:
                    ConnectionStrings__orders: "${conn:ordersdb}"
                    KAFKA_BOOTSTRAP: "${conn:broker}"
                    SPRING_DATASOURCE_URL: "jdbc:sqlserver://${conn:paydb.host}:${conn:paydb.port};databaseName=${conn:paydb.database};encrypt=false"
                    LOG_LEVEL: information
                    RETRY_COUNT: 8080
              dependencies:
                ordersdb:
                  type: postgres
                broker:
                  type: kafka
                paydb:
                  type: sqlserver
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var env = doc.Environment!.Services!["api"].Env;
        Assert.NotNull(env);
        Assert.Equal("${conn:ordersdb}", env!["ConnectionStrings__orders"]);
        Assert.Equal("${conn:broker}", env["KAFKA_BOOTSTRAP"]);
        Assert.Equal(
            "jdbc:sqlserver://${conn:paydb.host}:${conn:paydb.port};databaseName=${conn:paydb.database};encrypt=false",
            env["SPRING_DATASOURCE_URL"]);
        Assert.Equal("information", env["LOG_LEVEL"]);
        // Raw scalar form preserved — NOT coerced to an int/bool anywhere in this pipeline.
        Assert.Equal("8080", env["RETRY_COUNT"]);
    }

    [Fact]
    public void Parse_ServiceEnv_NonMappingNode_ThrowsYamlParseException()
    {
        // Arrange — 'env:' is a scalar, not a mapping.
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env: "not-a-mapping"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("env", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mapping", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Line > 0, "Line should be populated from the offending 'env' node.");
        Assert.True(ex.Column > 0, "Column should be populated from the offending 'env' node.");
    }

    [Fact]
    public void Parse_ServiceEnv_NonScalarValue_ThrowsYamlParseException()
    {
        // Arrange — an env entry whose value is a nested mapping, not a scalar string.
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    FOO:
                      nested: true
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("FOO", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scalar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Line > 0, "Line should be populated from the offending value node.");
        Assert.True(ex.Column > 0, "Column should be populated from the offending value node.");
    }

    [Fact]
    public void Parse_ServiceEnv_NonScalarKey_ThrowsYamlParseException()
    {
        // Arrange — an env mapping key that is itself a nested sequence, not a scalar name.
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    ? [a, b]
                    : "value"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("scalar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Line > 0, "Line should be populated from the offending key node.");
        Assert.True(ex.Column > 0, "Column should be populated from the offending key node.");
    }

    [Fact]
    public void Parse_ServiceEnv_AppliesToProjectFormServiceToo()
    {
        // Arrange — env: must be parsed identically for a project-form service.
        const string yaml = """
            environment:
              services:
                api:
                  project: "./Api/Api.csproj"
                  env:
                    ASPNETCORE_ENVIRONMENT: Development
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var env = doc.Environment!.Services!["api"].Env;
        Assert.NotNull(env);
        Assert.Equal("Development", env!["ASPNETCORE_ENVIRONMENT"]);
    }

    /// <summary>
    /// A dedicated lock-in for the YAML-scalar-coercion gotcha this parser is elsewhere
    /// careful about (code-review MINOR): YamlDotNet coerces a bare numeric/boolean scalar to
    /// its literal text form, and the parser must NOT further coerce it to int/bool anywhere
    /// in this pipeline — a container environment variable IS a string.
    /// </summary>
    [Fact]
    public void Parse_ServiceEnv_BareScalarCoercion_PreservesRawStringForm()
    {
        // Arrange
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    PORT: 8080
                    DEBUG: true
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var env = doc.Environment!.Services!["api"].Env;
        Assert.NotNull(env);
        Assert.Equal("8080", env!["PORT"]);
        Assert.Equal("true", env["DEBUG"]);
    }

    /// <summary>
    /// A duplicate <c>env:</c> mapping key locks in YamlDotNet's ACTUAL representation-model
    /// behaviour explicitly (code-review MINOR): it rejects a duplicate key outright
    /// (<c>YamlException</c> "Duplicate key") at the <c>YamlStream.Load</c> stage — there is
    /// no silent last-wins overwrite, for <c>env:</c> or any other mapping in the document.
    /// The existing top-level <c>catch (YamlException)</c> in <see cref="YamlDocumentParser.Parse"/>
    /// converts this to a <see cref="YamlParseException"/> with a located message, exactly like
    /// any other malformed-YAML input.
    /// </summary>
    [Fact]
    public void Parse_ServiceEnv_DuplicateKey_ThrowsYamlParseException()
    {
        // Arrange
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    FOO: first
                    FOO: second
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Managed-dependency configuration surface — dependency 'env:' mapping
    // (dependency-env spec, REQ-001 / EDGE-001 / EDGE-002).
    //
    // These tests drive YamlDocumentParser DIRECTLY rather than through
    // DocumentValidator, deliberately: the parser is the layer under test here, so
    // routing through the schema would put a second layer's verdict between the
    // assertion and the reader it is about.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DependencyEnv_Absent_IsNull()
    {
        // Arrange — a dependency with no 'env:' block at all: the shape every suite
        // written before this field has, whose behaviour must be unchanged.
        const string yaml = """
            environment:
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

        // Assert
        var dep = doc.Environment!.Dependencies!["orders-db"];
        Assert.Null(dep.Env);
        // …and nothing new appeared in the untyped bucket either.
        Assert.Null(dep.Extra);
    }

    /// <summary>
    /// The load-bearing half of REQ-001. A dependency's <c>env:</c> must land in the typed
    /// <see cref="DependencySpec.Env"/> field AND must NOT also fall through into
    /// <see cref="DependencySpec.Extra"/>: <c>Extra</c> is the untyped bucket for fields no
    /// typed field claims, and leaving a now-typed field in it is silent — nothing reads an
    /// <c>env</c> key out of <c>Extra</c> (<c>EnvironmentMapper</c> reads it only for
    /// <c>schemaRegistry</c>, <c>queues</c> and <c>topics</c>), so a duplicate there would
    /// look like a second, live configuration surface while being inert.
    /// </summary>
    [Fact]
    public void Parse_DependencyEnv_IsBoundToTypedField_AndNotLeakedIntoExtra()
    {
        // Arrange — two entries, because the assertion needs to distinguish "both bound" from
        // "one bound". The NAMES are deliberately meaningless: this test exercises the parser,
        // which reads a name as opaque text, so a realistic-looking name would document a usage
        // this test has not verified. Real variable names belong on a surface that has checked
        // them against the image the engine starts — see DependencySpec.Env's own remarks on why
        // the wrong name is worse than none.
        const string yaml = """
            environment:
              dependencies:
                dep:
                  type: mongodb
                  env:
                    PARSER_FIXTURE_A: first
                    PARSER_FIXTURE_B: second
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var dep = doc.Environment!.Dependencies!["dep"];
        Assert.NotNull(dep.Env);
        Assert.Equal(2, dep.Env!.Count);
        Assert.Equal("first", dep.Env["PARSER_FIXTURE_A"]);
        Assert.Equal("second", dep.Env["PARSER_FIXTURE_B"]);
        // The half that matters: 'env' is a typed field now, not an extra one.
        Assert.Null(dep.Extra);
    }

    [Fact]
    public void Parse_DependencyEnv_AlongsideTypeVersionAndImage_AllFourAreBoundToTypedFields()
    {
        // Arrange — every typed dependency field declared at once. The env NAME is meaningless
        // for the same reason as the mongo fixture above: this test proves only that the parser
        // routes each key to its typed field, so a real variable name here would be an
        // illustration with no observable effect: the edition-selecting variable this fixture
        // named before merely restated the edition the image already launches when it is unset.
        // (The name is described rather than spelled so that a future grep for it does not land
        // on this line and read it as a usage.)
        const string yaml = """
            environment:
              dependencies:
                sql:
                  type: sqlserver
                  version: "2022-latest"
                  image: nexus.example.com/mirror/mssql
                  env:
                    PARSER_FIXTURE_A: first
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var dep = doc.Environment!.Dependencies!["sql"];
        Assert.Equal("sqlserver", dep.Type);
        Assert.Equal("2022-latest", dep.Version);
        Assert.Equal("nexus.example.com/mirror/mssql", dep.Image);
        Assert.NotNull(dep.Env);
        Assert.Equal("first", dep.Env!["PARSER_FIXTURE_A"]);
        Assert.Null(dep.Extra);
    }

    /// <summary>
    /// Excluding <c>env</c> from the <c>Extra</c> bucket must remove exactly that one key — an
    /// unclaimed field declared alongside it (here <c>schemaRegistry</c>, which
    /// <c>EnvironmentMapper</c> reads) is still preserved in <see cref="DependencySpec.Extra"/>,
    /// unchanged.
    /// </summary>
    [Fact]
    public void Parse_DependencyEnv_UnrelatedExtraKey_StillLandsInExtra()
    {
        // Arrange — the env NAME is meaningless, per the mongo fixture above. What this fixture
        // named before was worse than merely unverified: it was a broker-side ZooKeeper
        // connection setting, and Aspire 13.4.2's AddKafka — which EnvironmentMapper calls for
        // type: kafka — starts confluentinc/confluent-local, a KRaft image with no ZooKeeper at
        // all (that variable family occurs nowhere in Aspire.Hosting.Kafka.dll). It documented a
        // topology this engine cannot stand up. The name is described rather than spelled so a
        // future grep for it does not land here and read it as a usage.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  schemaRegistry: true
                  env:
                    PARSER_FIXTURE_A: first
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var dep = doc.Environment!.Dependencies!["events"];
        Assert.NotNull(dep.Env);
        Assert.Equal("first", dep.Env!["PARSER_FIXTURE_A"]);

        Assert.NotNull(dep.Extra);
        var extraKeys = dep.Extra!.Children.Keys
            .OfType<YamlDotNet.RepresentationModel.YamlScalarNode>()
            .Select(k => k.Value)
            .ToList();
        Assert.Contains("schemaRegistry", extraKeys);
        Assert.DoesNotContain("env", extraKeys);
    }

    /// <summary>
    /// EDGE-001, first half — value shapes mirror the service rules exactly: a bare
    /// numeric/boolean scalar is retained as its RAW text ("8080"/"true"), which is exactly
    /// the literal text a container's environment variable needs. No numeric/boolean
    /// coercion is applied anywhere in this pipeline (the YAML-scalar-coercion gotcha this
    /// parser is elsewhere careful about).
    /// </summary>
    [Fact]
    public void Parse_DependencyEnv_BareScalarCoercion_PreservesRawStringForm()
    {
        // Arrange
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  env:
                    PORT: 8080
                    DEBUG: true
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        var env = doc.Environment!.Dependencies!["cache"].Env;
        Assert.NotNull(env);
        Assert.Equal("8080", env!["PORT"]);
        Assert.Equal("true", env["DEBUG"]);
    }

    /// <summary>
    /// EDGE-001, second half — an explicitly-null value ('FOO: ~'), and WHERE its rejection
    /// lives. MEASURED against the pinned YamlDotNet 16.3.0 representation model: a '~'
    /// scalar reads back as the LITERAL one-character text "~", for the service path and the
    /// dependency path alike — this parser rejects neither, it retains both verbatim, exactly
    /// as it retains every other raw scalar. The rejection EDGE-001 requires is the JSON
    /// Schema's ('env' values are typed 'string | integer | number | boolean', which admits no
    /// null), a shape $defs/dependency now carries too; mirroring the service rules here
    /// therefore means mirroring this retention, not
    /// inventing a parser-level refusal the service path does not have. Asserted as a PAIR so
    /// that if either path ever starts folding '~' to null, the two go red together rather
    /// than diverging silently.
    /// </summary>
    [Fact]
    public void Parse_Env_ExplicitNullValue_IsRetainedAsLiteralTilde_ForServiceAndDependencyAlike()
    {
        const string serviceYaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    FOO: ~
            steps:
              - id: noop
                type: script.csharp
            """;
        const string dependencyYaml = """
            environment:
              dependencies:
                api:
                  type: postgres
                  env:
                    FOO: ~
            steps:
              - id: noop
                type: script.csharp
            """;

        var serviceEnv = YamlDocumentParser.Parse(serviceYaml).Environment!.Services!["api"].Env;
        var dependencyEnv = YamlDocumentParser.Parse(dependencyYaml).Environment!.Dependencies!["api"].Env;

        Assert.NotNull(serviceEnv);
        Assert.NotNull(dependencyEnv);
        Assert.Equal("~", serviceEnv!["FOO"]);
        Assert.Equal("~", dependencyEnv!["FOO"]);
    }

    /// <summary>
    /// EDGE-002 — an empty <c>env: {}</c> is accepted, applies nothing, and is
    /// DISTINGUISHABLE from a dependency that declared no <c>env:</c> at all (the second
    /// direction is asserted by <see cref="Parse_DependencyEnv_Absent_IsNull"/> and again
    /// here, side by side, so the pair cannot silently collapse to one representation).
    /// </summary>
    [Fact]
    public void Parse_DependencyEnv_EmptyMap_IsAcceptedAndDistinguishableFromAbsent()
    {
        // Arrange — two dependencies differing ONLY in whether 'env:' was written.
        const string yaml = """
            environment:
              dependencies:
                declared-empty:
                  type: postgres
                  env: {}
                absent:
                  type: postgres
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — accepted, applies nothing, and not the same value as absent.
        var declaredEmpty = doc.Environment!.Dependencies!["declared-empty"];
        var absent = doc.Environment.Dependencies["absent"];

        Assert.NotNull(declaredEmpty.Env);
        Assert.Empty(declaredEmpty.Env!);
        Assert.Null(absent.Env);
        // The declared-but-empty spelling does not leak into the untyped bucket either. (The
        // 'absent' half is not asserted here: a dependency that writes no 'env:' cannot detect
        // an env-leaks-into-Extra regression, and Parse_DependencyEnv_Absent_IsNull already
        // pins that it acquires no Extra.)
        Assert.Null(declaredEmpty.Extra);
    }

    /// <summary>
    /// EDGE-002's other half, and the ONLY test that pins the service side of the
    /// asymmetry. <see cref="Parse_ServiceEnv_Absent_IsNull"/> covers an ABSENT service
    /// <c>env:</c>, not an empty one, so before this test nothing anywhere fed
    /// <c>env: {}</c> to a service and <c>ParseServiceMap</c>'s empty-to-<see langword="null"/>
    /// collapse could be deleted with every suite still green.
    /// </summary>
    [Fact]
    public void Parse_Env_EmptyMap_CollapsesToNullOnAServiceButIsRetainedOnADependency()
    {
        // The deliberate asymmetry (dependency-env EDGE-002): changing service 'env:' behaviour
        // in any way is out of scope for that change, and 'env: {}' on a service has always
        // collapsed to null, so it must keep doing so. A dependency has no such history, so it
        // distinguishes the two. Both halves asserted here, from ONE document, so a refactor
        // that "tidies" either one — collapsing the dependency's empty map, or promoting the
        // service's null — goes red.
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env: {}
              dependencies:
                db:
                  type: postgres
                  env: {}
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — same spelling, two representations, on purpose.
        var services = doc.Environment!.Services!;
        var dependencies = doc.Environment.Dependencies!;

        Assert.Null(services["api"].Env);
        Assert.NotNull(dependencies["db"].Env);
        Assert.Empty(dependencies["db"].Env!);
    }

    // -------------------------------------------------------------------------
    // Widened env-map diagnostics — a dependency fault must read "Dependency 'x' …"
    // where the identical service fault reads "Service 'x' …", and must differ in
    // NOTHING else (dependency-env spec, "Out of scope": widening ParseEnvMap's
    // signature leaves the service diagnostics byte-identical apart from the noun).
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DependencyEnv_NonMappingNode_ThrowsYamlParseException()
    {
        // Arrange — 'env:' is a scalar, not a mapping.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  env: "not-a-mapping"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("Dependency 'orders-db'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Service", ex.Message, StringComparison.Ordinal);
        // The 'env' KEY by name, quoted and at its position — not a bare "env" substring,
        // which the message's own word "environment-variable" supplies whether or not the
        // key is ever named.
        Assert.Contains("'env' at line", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mapping", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The exact position, not merely a positive one: the fault must be attributed to the
        // offending 'env' scalar itself (line 5, at its opening quote) and not to the parent
        // dependency mapping two lines up — the regression an 'ex.Line > 0' assertion cannot
        // see, since YamlDotNet's Mark is 1-based and no throw site here can produce 0.
        Assert.Equal(5, ex.Line);
        Assert.Equal(12, ex.Column);
    }

    [Fact]
    public void Parse_DependencyEnv_NonScalarValue_ThrowsYamlParseException()
    {
        // Arrange — an env entry whose value is a nested mapping, not a scalar string.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  env:
                    FOO:
                      nested: true
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("Dependency 'orders-db'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FOO", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scalar", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The nested mapping's own position (line 7, at 'nested'), not the 'FOO:' key's line
        // above it and not the enclosing 'env:' — see the position note on
        // Parse_DependencyEnv_NonMappingNode_ThrowsYamlParseException.
        Assert.Equal(7, ex.Line);
        Assert.Equal(11, ex.Column);
    }

    [Fact]
    public void Parse_DependencyEnv_NonScalarKey_ThrowsYamlParseException()
    {
        // Arrange — an env mapping key that is itself a nested sequence, not a scalar name.
        const string yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  env:
                    ? [a, b]
                    : "value"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act + Assert
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("Dependency 'orders-db'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scalar", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The offending KEY node's own position (line 6, at the '[' opening the flow
        // sequence), not the enclosing 'env:' mapping's — see the position note on
        // Parse_DependencyEnv_NonMappingNode_ThrowsYamlParseException.
        Assert.Equal(6, ex.Line);
        Assert.Equal(11, ex.Column);
    }

    /// <summary>
    /// The binding constraint on widening the env-map reader: ONE implementation, two
    /// subject nouns. Two documents identical in shape — same owner name, same offending
    /// node at the same line and column — must produce diagnostics differing in the subject
    /// noun and in NOTHING else. Asserted by textual substitution rather than by two
    /// hand-written expected strings, so the pair cannot drift apart one word at a time, and
    /// applied to ALL THREE of the reader's throw sites (non-mapping <c>env:</c>, non-scalar
    /// key, non-scalar value) rather than only the first.
    /// </summary>
    /// <remarks>
    /// WHAT THIS TEST DOES NOT MEASURE, stated plainly because the spec's out-of-scope fence
    /// is worded more strongly than any test can be: the fence is that the SERVICE diagnostics
    /// are byte-identical to their PRE-WIDENING form. That is a fact about the diff — all three
    /// throw sites changed the subject noun and nothing else — and it is verifiable only by
    /// reading the diff, because a test can only see the tree it runs against. What this test
    /// pins is the FORWARD-GOING parity of the two nouns: reword both messages in lockstep and
    /// it still passes. It is the right invariant to automate (one implementation, two nouns,
    /// no per-noun drift); it is not a baseline lock.
    /// </remarks>
    [Fact]
    public void Parse_Env_ServiceAndDependencyDiagnostics_DifferOnlyBySubjectNoun()
    {
        // Non-mapping 'env:' — deliberately shape-identical: owner 'api', 'env:' on the same
        // line at the same column in both, offending value written identically. The first
        // argument names the throw site each pair is meant to reach, so a pair that silently
        // stopped exercising its own site (and started duplicating another) goes red rather
        // than passing three times over one diagnostic.
        AssertDiffersOnlyBySubjectNoun(
            "'env' at line 5 must be a mapping",
            """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env: "not-a-mapping"
            steps:
              - id: noop
                type: script.csharp
            """,
            """
            environment:
              dependencies:
                api:
                  type: postgres
                  env: "not-a-mapping"
            steps:
              - id: noop
                type: script.csharp
            """);

        // Non-scalar env KEY.
        AssertDiffersOnlyBySubjectNoun(
            "'env' key at line 6 must be a scalar",
            """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    ? [a, b]
                    : "value"
            steps:
              - id: noop
                type: script.csharp
            """,
            """
            environment:
              dependencies:
                api:
                  type: postgres
                  env:
                    ? [a, b]
                    : "value"
            steps:
              - id: noop
                type: script.csharp
            """);

        // Non-scalar env VALUE.
        AssertDiffersOnlyBySubjectNoun(
            "env entry 'FOO' at line 7 must be a scalar",
            """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    FOO:
                      nested: true
            steps:
              - id: noop
                type: script.csharp
            """,
            """
            environment:
              dependencies:
                api:
                  type: postgres
                  env:
                    FOO:
                      nested: true
            steps:
              - id: noop
                type: script.csharp
            """);

        static void AssertDiffersOnlyBySubjectNoun(
            string expectedSiteFragment, string serviceYaml, string dependencyYaml)
        {
            var serviceEx = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(serviceYaml));
            var dependencyEx = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(dependencyYaml));

            Assert.Contains(expectedSiteFragment, serviceEx.Message, StringComparison.Ordinal);
            Assert.StartsWith("Service 'api'", serviceEx.Message, StringComparison.Ordinal);
            Assert.Equal(
                serviceEx.Message.Replace("Service 'api'", "Dependency 'api'", StringComparison.Ordinal),
                dependencyEx.Message);
            // Column only. Line parity is already implied by the message equality above,
            // since every one of these messages embeds "at line N"; Column is not in any
            // message text, so this is the assertion carrying weight here.
            Assert.Equal(serviceEx.Column, dependencyEx.Column);
        }
    }

    // -------------------------------------------------------------------------
    // Endpoint selection for a project-form service — service 'endpoint:' scalar
    //
    // These tests assert exactly what this layer owns — that the key is bound to
    // the typed field, that its absence is null, that the text is carried through
    // untouched, and that the binding does not depend on the service's form.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ServiceEndpoint_ParsesIntoTypedField()
    {
        // Arrange — a project-form service naming one of its launch-profile endpoints.
        const string yaml = """
            environment:
              services:
                api:
                  project: "./Api/Api.csproj"
                  endpoint: https
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        Assert.Equal("https", doc.Environment!.Services!["api"].Endpoint);
    }

    /// <summary>
    /// The parser is deliberately BLIND to the service's form: it binds <c>endpoint:</c> on an
    /// <c>image:</c>-form service exactly as it does on a <c>project:</c>-form one. The form
    /// restriction is not this layer's job — the JSON Schema refuses the combination for any
    /// document that goes through validation, and REQ-006's eager check in
    /// <c>EnvironmentMapper</c> refuses it again for the direct-construction path that bypasses
    /// the schema. This test exists because that second backstop is asserted by constructing an
    /// <c>EnvironmentSpec</c> in code and so never touches the parser: were the parser "helpfully"
    /// taught to drop <c>endpoint:</c> on an image-form service, every existing test would stay
    /// green while the mapper's refusal silently stopped firing on real documents, turning a
    /// located error into a silent discard.
    /// </summary>
    [Fact]
    public void Parse_ServiceEndpoint_ImageForm_StillBinds_BecauseFormPolicyLivesElsewhere()
    {
        // Arrange — an image-form service declaring 'endpoint:', which the schema refuses but the
        // parser must still bind, so the layer that owns the refusal has something to refuse.
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  endpoint: https
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        Assert.Equal("https", doc.Environment!.Services!["api"].Endpoint);
    }

    [Fact]
    public void Parse_ServiceEndpoint_Absent_IsNull()
    {
        // Arrange — a service with no 'endpoint:' key at all, which is every service
        // written before this field existed.
        const string yaml = """
            environment:
              services:
                api:
                  project: "./Api/Api.csproj"
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        Assert.Null(doc.Environment!.Services!["api"].Endpoint);
    }

    /// <summary>
    /// The scalar's text is carried VERBATIM: no trimming, no case folding, no normalisation
    /// of any kind. The value is matched against the names a project's launch profile declares
    /// under <c>StringComparison.Ordinal</c>, so any normalisation applied here would silently
    /// change which listener the author selected — and would hide a mistyped selector that the
    /// consuming layer is meant to refuse by name. Asserted with a value that both a trim and
    /// a case fold would visibly alter.
    /// </summary>
    [Fact]
    public void Parse_ServiceEndpoint_IsRetainedVerbatim()
    {
        // Arrange — a double-quoted scalar, which preserves the surrounding whitespace YAML
        // would otherwise strip from a plain one.
        const string yaml = """
            environment:
              services:
                api:
                  project: "./Api/Api.csproj"
                  endpoint: "  HTTPS  "
            steps:
              - id: noop
                type: script.csharp
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert
        Assert.Equal("  HTTPS  ", doc.Environment!.Services!["api"].Endpoint);
    }
}
