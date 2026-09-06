// Permanent characterisation suite for $defs/security's closed property surface (REQ-020,
// authenticated-infrastructure-mtls slice C): "unevaluatedProperties": false REPLACING
// "additionalProperties": false (root-language-schema.json's own $defs/security), never
// declared alongside it.
//
// Mirrors SchemaStepSurfaceClosureTests' own spike methodology (that file's header records
// the trap this class re-proves for $defs/security specifically): mutate an in-memory copy of
// the REAL composed schema, evaluate against it directly, never assert from reasoning alone.
//
// Two things this class proves, empirically, against JsonSchema.Net 9.2.1:
//   1. A synthetic profile fragment's own if/then clause, appended to $defs/security's own
//      allOf, can introduce a NEW field and have it correctly recognised as evaluated by
//      unevaluatedProperties — the whole point of REQ-020: a contributed profile fragment can
//      compose into $defs/security today, something additionalProperties structurally could
//      never support (it only ever sees ITS OWN 'properties', never a sibling allOf/if/then's).
//   2. The sibling-keyword trap SchemaStepSurfaceClosureTests documents for $defs/step recurs
//      identically for $defs/security: reintroducing "additionalProperties": true alongside
//      "unevaluatedProperties": false in the SAME schema object is a silent, total no-op per
//      JSON Schema 2020-12 — kept alive here as a JsonSchema.Net-upgrade regression guard,
//      exactly as SchemaStepSurfaceClosureTests' own finding-2 guard does for $defs/step.
//
// And one thing that used to stop at the schema and no longer does (#353). Finding 1 shows a
// composed profile fragment's own field validating; until the fix, nothing downstream could read
// it, because YamlDocumentParser.ParseSecurity bound a fixed set of keys into SecuritySpec and
// that record had no Extra bucket (DependencySpec did). It has one now, and
// AComposedProfileFragmentField_SurvivesParsingIntoTheExtraBucket at the foot of this file proves
// the survival by round-tripping such a field through the real parser rather than by reflecting
// over the record's shape.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Permanent characterisation suite for <c>$defs/security</c>'s closed property surface
/// (<c>"unevaluatedProperties": false</c>, REQ-020). See the file header for the full brief.
/// </summary>
public sealed class SchemaSecuritySurfaceClosureTests
{
    private static readonly EvaluationOptions s_options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    // A minimal registry: $defs/security is part of the ROOT schema, not provider-composed,
    // so no Core-catalogue breadth is needed here — only a single registered type for the
    // filler step every fixture below carries.
    private static StepKindRegistry MinimalRegistry() =>
        StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

    // ── Finding 1: a composed profile fragment's own field is recognised as evaluated ──

    /// <summary>
    /// Injects a SYNTHETIC profile fragment — an <c>allOf</c>/<c>if</c>/<c>then</c> clause
    /// keyed on a hypothetical <c>profile: acme-sasl</c>, declaring a field
    /// (<c>saslMechanism</c>) that exists nowhere in the shipped schema — into an in-memory
    /// copy of the REAL composed schema's <c>$defs/security</c>, and asserts an instance
    /// carrying that field validates. This is the evidence REQ-020 exists to produce: a
    /// contributed profile fragment can compose into <c>$defs/security</c> at all, proven
    /// empirically against JsonSchema.Net rather than reasoned from the specification text.
    /// </summary>
    [Fact]
    public void SyntheticProfileFragment_InjectedIntoSecurityAllOf_ItsOwnFieldValidates()
    {
        var registry = MinimalRegistry();
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var rootObj = JsonNode.Parse(composedJson)!.AsObject();
        var securityDef = rootObj["$defs"]!["security"]!.AsObject();

        // Finding 2's own precondition, re-verified here too: the REAL, shipped $defs/security
        // already carries unevaluatedProperties and does NOT carry additionalProperties — if
        // this ever regressed, the injected fragment's field below would be swept away
        // regardless of the injection, and this test would fail for the WRONG reason.
        Assert.True(securityDef.ContainsKey("unevaluatedProperties"),
            "Expected the real $defs/security to already carry 'unevaluatedProperties'.");
        Assert.False(securityDef.ContainsKey("additionalProperties"),
            "Expected the real $defs/security to NOT carry 'additionalProperties' — REQ-020 " +
            "replaces the keyword, it never adds the new one alongside the old.");

        var securityAllOf = securityDef["allOf"]!.AsArray();
        securityAllOf.Add(JsonNode.Parse("""
            {
              "if": { "properties": { "profile": { "const": "acme-sasl" } } },
              "then": { "properties": { "saslMechanism": { "type": "string" } } }
            }
            """));

        var mutatedSchema = JsonSchema.FromText(rootObj.ToJsonString());

        const string instance = """
            {
              "environment": {
                "services": {
                  "app": {
                    "image": "myorg/app:1.0",
                    "security": {
                      "profile": "acme-sasl",
                      "endpoint": 9092,
                      "saslMechanism": "PLAIN"
                    }
                  }
                }
              },
              "steps": [
                { "id": "noop", "type": "script.csharp", "code": "// noop" }
              ]
            }
            """;

        using var doc = JsonDocument.Parse(instance);
        var results = mutatedSchema.Evaluate(doc.RootElement, s_options);

        Assert.True(results.IsValid,
            "Expected the synthetic profile fragment's own 'saslMechanism' field to be " +
            "ACCEPTED (recognised as evaluated by unevaluatedProperties), not swept away as " +
            $"unknown. Raw result: {results}");
    }

    /// <summary>
    /// The negative control for the test above: the SAME synthetic <c>saslMechanism</c>
    /// field, declared WITHOUT the matching <c>profile: acme-sasl</c> discriminator, must
    /// still be rejected as unevaluated — proving the field is genuinely gated by the
    /// injected <c>if</c>/<c>then</c>, not merely always accepted regardless of context (which
    /// would make the positive test above vacuous).
    /// </summary>
    [Fact]
    public void SyntheticProfileFragment_FieldWithoutMatchingProfile_IsStillRejected()
    {
        var registry = MinimalRegistry();
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var rootObj = JsonNode.Parse(composedJson)!.AsObject();
        var securityDef = rootObj["$defs"]!["security"]!.AsObject();
        var securityAllOf = securityDef["allOf"]!.AsArray();
        securityAllOf.Add(JsonNode.Parse("""
            {
              "if": { "properties": { "profile": { "const": "acme-sasl" } } },
              "then": { "properties": { "saslMechanism": { "type": "string" } } }
            }
            """));

        var mutatedSchema = JsonSchema.FromText(rootObj.ToJsonString());

        // 'profile: tls' this time — the synthetic if-clause's own condition never matches,
        // so its then-branch's 'saslMechanism' annotation never propagates, and the field
        // must be reported as unevaluated.
        const string instance = """
            {
              "environment": {
                "services": {
                  "app": {
                    "image": "myorg/app:1.0",
                    "security": {
                      "profile": "tls",
                      "endpoint": 9092,
                      "saslMechanism": "PLAIN"
                    }
                  }
                }
              },
              "steps": [
                { "id": "noop", "type": "script.csharp", "code": "// noop" }
              ]
            }
            """;

        using var doc = JsonDocument.Parse(instance);
        var results = mutatedSchema.Evaluate(doc.RootElement, s_options);

        Assert.False(results.IsValid,
            "Expected 'saslMechanism' to be rejected as unevaluated when the synthetic " +
            "fragment's own discriminator ('profile: acme-sasl') does not match — otherwise " +
            "the positive companion test proves nothing.");

        // G-MINOR-3 (gatekeeper): a bare IsValid == false is not enough — this negative
        // control's WHOLE POINT is proving the positive test above is not vacuous, so it must
        // not itself pass for an unrelated reason (e.g. a stray typo elsewhere in the mutated
        // instance). Pin the exact rejected location to the field this test is actually about.
        var errors = SchemaErrorCollector.CollectErrors(results);
        Assert.Contains(errors, e => e.InstanceLocation == "/environment/services/app/security/saslMechanism");
    }

    // ── Finding 2: the sibling-keyword trap, kept alive as a regression guard ──────────

    /// <summary>
    /// Production code no longer contains the "naive fix" mistake to observe directly
    /// ($defs/security REPLACES, not adds alongside), so this guard re-creates it on an
    /// in-memory MUTATED COPY of the real composed schema: adds
    /// <c>"additionalProperties": true</c> back onto <c>$defs/security</c> as a SIBLING of
    /// its existing <c>"unevaluatedProperties": false</c>. Per JSON Schema 2020-12, this must
    /// still be a silent, total no-op — if a future JsonSchema.Net upgrade changed that
    /// same-object-cancellation rule, this test would start failing and name exactly why.
    /// Mirrors <c>SchemaStepSurfaceClosureTests.NaiveFix_SiblingAdditionalPropertiesOnStepDef_IsStillASilentNoOp</c>
    /// one $def down.
    /// </summary>
    [Fact]
    public void NaiveFix_SiblingAdditionalPropertiesOnSecurityDef_IsStillASilentNoOp()
    {
        var registry = MinimalRegistry();
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var rootObj = JsonNode.Parse(composedJson)!.AsObject();
        var securityDef = rootObj["$defs"]!["security"]!.AsObject();

        Assert.True(securityDef.ContainsKey("unevaluatedProperties"),
            "Expected $defs/security to already carry 'unevaluatedProperties' before this " +
            "guard reintroduces the sibling trap.");
        Assert.False(securityDef.ContainsKey("additionalProperties"),
            "Expected $defs/security to NOT already carry 'additionalProperties' — this " +
            "guard adds it back deliberately.");

        securityDef["additionalProperties"] = JsonValue.Create(true);
        var mutatedSchema = JsonSchema.FromText(rootObj.ToJsonString());

        // An unrecognised key ('bogus') that would normally be rejected by
        // unevaluatedProperties: false. Declared on a KAFKA dependency (M1, second
        // peer-review round): on any other kind the whole 'security' block is now rejected by
        // $defs/dependency's own final allOf clause, which would make this instance invalid
        // regardless of the sibling-keyword trap — the WRONG reason, leaving the guard
        // vacuous. Kafka accepts the block, so the only thing that can decide this
        // instance's validity is the keyword cancellation this guard is about.
        const string instance = """
            {
              "environment": {
                "dependencies": {
                  "events": {
                    "type": "kafka",
                    "security": {
                      "profile": "tls",
                      "endpoint": 9093,
                      "bogus": true
                    }
                  }
                }
              },
              "steps": [
                { "id": "noop", "type": "script.csharp", "code": "// noop" }
              ]
            }
            """;

        using var doc = JsonDocument.Parse(instance);
        var results = mutatedSchema.Evaluate(doc.RootElement, s_options);

        Assert.True(results.IsValid,
            "Expected the reintroduced sibling additionalProperties:true to still cancel " +
            "unevaluatedProperties (a silent no-op) — if this now fails, JsonSchema.Net's " +
            "same-object-cancellation behaviour has changed and the production schema's " +
            "REPLACE (not ADD) discipline needs re-verifying.");
    }

    // ── G-POSITIVE: pin the cascade that did NOT happen on the environment surface ────

    /// <summary>
    /// G-POSITIVE (gatekeeper): switching <c>$defs/security</c> to <c>unevaluatedProperties:
    /// false</c> did NOT trigger the same annotation-dropping cascade that forced
    /// <c>SchemaErrorCollector.SuppressUnevaluatedPropertiesCascade</c> into existence
    /// for <c>$defs/step</c> — verified empirically (see the class remarks and this file's own
    /// Finding 1/2 above) — but <c>SuppressUnevaluatedPropertiesCascade</c>'s OWN scoping is
    /// deliberately confined to the step surface (<see cref="SchemaErrorCollector.TryGetStepScope"/>
    /// only ever recognises <c>/steps/&lt;N&gt;</c>), so the environment/security surface has NO
    /// equivalent cascade protection today. Nothing currently NEEDS one — each of these five
    /// single-field shapes yields exactly one author-facing error already: four because only one
    /// keyword fails, the fifth because the same-location clause subsumes the second — but
    /// nothing GUARDS that either, and a JsonSchema.Net upgrade changing same-object
    /// annotation-propagation behaviour (exactly the risk <c>NaiveFix_*</c> above pins for the
    /// sibling-keyword trap) could silently turn one genuine defect into a false "unknown
    /// property" pile-up alongside it, with no test here to notice. Pins single-error output,
    /// through the real <see cref="DocumentValidator"/> front door, for the five shapes
    /// measured clean: a blank <c>caCert</c>, an out-of-range <c>endpoint</c>, a literal
    /// <c>clientKeyPassword</c> under <c>mtls</c>, the same literal under <c>tls</c> (where the
    /// field is forbidden outright, so TWO keywords fail at one location), and a nested
    /// <c>serverArtifacts[0].target</c> pattern miss.
    /// </summary>
    /// <remarks>
    /// The caCert and endpoint fixtures moved from a redis dependency to a kafka one in M1
    /// (second peer-review round): a 'security' block on any non-kafka dependency kind is now
    /// rejected outright, which would make those documents single-error for a reason that has
    /// nothing to do with an annotation cascade — the guard would still be green while
    /// proving nothing. Kafka accepts the block, so the single error each fixture yields is
    /// still the one direct-field defect it declares. The serverArtifacts fixture was already
    /// on kafka, and both clientKeyPassword fixtures were written on kafka for the same reason.
    /// The whole-block rejection's OWN single-error behaviour is pinned separately, by
    /// <c>EnvironmentSchemaTests.Dependency_Security_OnNonKafkaKind_YieldsExactlyOneError_WhateverIsInsideTheBlock</c>.
    /// </remarks>
    [Theory]
    [InlineData(
        // A declared-but-blank caCert fails $defs/security.properties.caCert's own
        // 'minLength: 1' — a single, direct-field defect.
        """
        environment:
          dependencies:
            events:
              type: kafka
              security:
                profile: tls
                endpoint: 9093
                caCert: ""
        steps:
          - id: noop
            type: script.csharp
            code: "// noop"
        """)]
    [InlineData(
        // A bare (unquoted) out-of-range port fails $defs/security.properties.endpoint's own
        // 'maximum: 65535'.
        """
        environment:
          dependencies:
            events:
              type: kafka
              security:
                profile: tls
                endpoint: 70000
        steps:
          - id: noop
            type: script.csharp
            code: "// noop"
        """)]
    [InlineData(
        // A literal (non-'${secret:}') clientKeyPassword fails
        // $defs/security.properties.clientKeyPassword's own 'pattern' — the same shape as the
        // blank-caCert case above, on the field this surface gained most recently. Present
        // because that field is the newest addition to a $def with NO
        // SuppressUnevaluatedPropertiesCascade protection, so it is exactly where a
        // single-defect-to-error-pile-up regression would first show.
        """
        environment:
          dependencies:
            events:
              type: kafka
              security:
                profile: mtls
                endpoint: 9093
                clientCert: certs/client.pem
                clientKey: certs/client.key
                clientKeyPassword: "hunter2"
        steps:
          - id: noop
            type: script.csharp
            code: "// noop"
        """)]
    [InlineData(
        // The SAME literal, under 'profile: tls', where 'clientKeyPassword' is forbidden
        // outright by this $def's own allOf. Unlike every other case in this theory, TWO
        // keywords fail at ONE location here: the boolean 'false' subschema ([properties])
        // and the field's own 'pattern'. That two-into-one shape is NOT new to this field —
        // measured against the composed schema, 'profile: tls' with 'clientCert: ""'
        // (minLength) or with 'clientCert: 123' (type) produces the identical two errors at one
        // pointer, collected to one, and has done since the tls branch existed. What IS new is
        // narrower: 'clientKeyPassword' is the first forbidden-under-tls scalar carrying a
        // 'pattern' (clientCert and clientKey carry only minLength and type), and the first for
        // which this corpus exercises the shape at all — the clientCert fixtures elsewhere
        // declare a valid path, which passes minLength. The author's one action is to delete
        // the field, so one error is the correct output — measured here, never inferred.
        """
        environment:
          dependencies:
            events:
              type: kafka
              security:
                profile: tls
                endpoint: 9093
                clientKeyPassword: "hunter2"
        steps:
          - id: noop
            type: script.csharp
            code: "// noop"
        """)]
    [InlineData(
        // A non-absolute serverArtifacts[].target fails $defs/securityServerArtifact's own
        // 'pattern: "^/"' — a defect nested TWO levels below the security object itself.
        """
        environment:
          dependencies:
            events:
              type: kafka
              security:
                profile: tls
                endpoint: 9093
                serverArtifacts:
                  - source: certs/broker.jks
                    target: certs/broker.jks
        steps:
          - id: noop
            type: script.csharp
            code: "// noop"
        """)]
    public void SingleEnvironmentSecurityDefect_YieldsExactlyOneError(string yaml)
    {
        var registry = MinimalRegistry();

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, $"Expected this document to be rejected. Got: {yaml}");
        Assert.Single(result.Errors);
    }

    // ── Where the REQ-020 seam reaches: through the schema and into the model ─────────

    /// <summary>
    /// #353: REQ-020's composed-profile-fragment seam is proven at the SCHEMA layer by Finding 1
    /// above, and this row proves the NEXT layer keeps the data. A <c>security</c> key
    /// <c>YamlDocumentParser.ParseSecurity</c> binds to no typed member survives parsing into
    /// <see cref="SecuritySpec.Extra"/>, readable there rather than discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This row replaces the reflection-only assertion that preceded it — which pinned the
    /// bucket's ABSENCE, with <c>DependencySpec</c> as a can-detect-a-bucket control. Inverting
    /// that assertion would have proved only that a property called <c>Extra</c> exists, which is
    /// not the behaviour #353 is about: a bucket the parser never fills is the same drop with an
    /// extra member. So the round trip is through the real parser. The
    /// can-detect-a-bucket control that assertion carried moved to its own row below, where it
    /// still controls something (that the shape this bucket was modelled on has not moved) rather
    /// than sitting inside a test named for a parser round trip.
    /// </para>
    /// <para>
    /// The fixture is parsed but deliberately NOT validated: the real schema refuses an unknown
    /// key inside <c>security</c> (Finding 1's whole point — the seam is for a field a composed
    /// profile fragment CONTRIBUTES, and no such fragment has shipped), while the parser is
    /// lenient by design. That asymmetry is what makes the bucket observable today, and it is
    /// exactly the state a contributed field would arrive in once a fragment does ship.
    /// </para>
    /// </remarks>
    [Fact]
    public void AComposedProfileFragmentField_SurvivesParsingIntoTheExtraBucket()
    {
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: acme.spiffe
                    endpoint: 9093
                    trustDomain: spiffe://acme.example
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var document = YamlDocumentParser.Parse(yaml);
        var security = document.Environment?.Dependencies?["events"].Security;

        Assert.NotNull(security);

        // The typed members the parser DOES bind are unaffected — the bucket takes what is left
        // over, never a key a member claims.
        Assert.Equal("acme.spiffe", security!.Profile);
        Assert.Equal("9093", security.Endpoint);

        Assert.NotNull(security.Extra);
        Assert.True(
            security.Extra!.Children.TryGetValue(new YamlScalarNode("trustDomain"), out var value),
            "A 'security' key ParseSecurity binds to no typed member must be readable from " +
            "SecuritySpec.Extra. Finding it absent means the field validated (once a profile " +
            "fragment declares it) and was then dropped before any consumer could see it — the " +
            "state #353 closed. Bind it in ParseSecurity's BuildExtraNode call.");
        Assert.Equal("spiffe://acme.example", ((YamlScalarNode)value).Value);

        // The bucket holds ONLY the unbound key: were an exclusion missing from that call, a
        // bound key would appear here as well as in its typed member and nothing else would say so.
        Assert.Single(security.Extra.Children);

        // And the member census, which notices any member VANISHING from the record.
        var declared = typeof(SecuritySpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(s_securitySpecMembers, declared);
    }

    /// <summary>
    /// <see cref="Vouchfx.Engine.Authoring.Model.DependencySpec"/> still carries the <c>Extra</c>
    /// bucket <see cref="SecuritySpec.Extra"/> was modelled on (#353) — same type, same
    /// null-not-empty contract, same ordinal key comparison.
    /// </summary>
    /// <remarks>
    /// Its own row rather than a preamble to the round-trip test above, where it was a leftover
    /// control for an assertion that no longer exists. If the dependency bucket is ever retyped or
    /// removed, the security bucket's own documentation — which describes it as that record's
    /// counterpart throughout — needs revisiting, and this is what says so.
    /// </remarks>
    [Fact]
    public void TheDependencyBucketSecuritysIsModelledOn_StillExists()
    {
        var extra = typeof(Vouchfx.Engine.Authoring.Model.DependencySpec)
            .GetProperty("Extra", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(extra);
        Assert.Equal(
            typeof(SecuritySpec).GetProperty("Extra", BindingFlags.Public | BindingFlags.Instance)!
                .PropertyType,
            extra!.PropertyType);
    }

    /// <summary>
    /// The direction the round-trip test cannot see: a key named in <c>ParseSecurity</c>'s
    /// EXCLUSION list that the method does not actually bind is dropped by both halves — excluded
    /// from the bucket, and read into no member — which is the accept-and-drop state #353 closed,
    /// silently reopened for one key.
    /// </summary>
    /// <remarks>
    /// Derived, not hand-listed: the exclusion list is exercised through the parser by declaring
    /// each <c>$defs/security</c> property ALONE and asserting the result is observable somewhere
    /// — in a typed member or in the bucket. A spurious name in that list fails here for the key
    /// that lost its binding, naming it.
    /// </remarks>
    [Fact]
    public void EverySecuritySchemaProperty_IsObservableAfterParsing_InAMemberOrInTheBucket()
    {
        var registry = MinimalRegistry();
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var schemaProperties = JsonNode.Parse(composedJson)!.AsObject()["$defs"]!["security"]!
            .AsObject()["properties"]!.AsObject()
            .Select(property => property.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(schemaProperties);

        // A type-appropriate value per key, so each declaration is one the parser can bind.
        // Keyed by the schema property name, and the fixture is asserted COMPLETE below rather
        // than trusted, so a new property cannot slip past with no value of its own.
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile"] = "mtls",
            ["endpoint"] = "9093",
            ["caCert"] = "./certs/ca.pem",
            ["clientCert"] = "./certs/client.pem",
            ["clientKey"] = "./certs/client.key",
            ["clientKeyPassword"] = "\"${secret:env/PASS}\"",
            ["serverArtifacts"] = "\n          - source: ./certs/broker.jks\n            target: /etc/broker.jks",
        };

        var missing = schemaProperties.Where(key => !values.ContainsKey(key)).ToArray();
        Assert.True(
            missing.Length == 0,
            "$defs/security declares " + string.Join(", ", missing) + ", for which this test has " +
            "no fixture value. Add one — otherwise the key is not exercised and a spurious " +
            "exclusion for it would go unnoticed.");

        foreach (var key in schemaProperties)
        {
            var yaml = $"""
                environment:
                  dependencies:
                    events:
                      type: kafka
                      security:
                        {key}: {values[key]}
                steps:
                  - id: noop
                    type: script.csharp
                    code: "// noop"
                """;

            var security = YamlDocumentParser.Parse(yaml)
                .Environment?.Dependencies?["events"].Security;

            Assert.True(
                security is not null,
                $"A 'security' block declaring only '{key}' produced no SecuritySpec at all.");

            var member = typeof(SecuritySpec)
                .GetProperty(ToPascalCase(key), BindingFlags.Public | BindingFlags.Instance);

            var boundToAMember = member?.GetValue(security) is not null;
            var boundToTheBucket = security!.Extra is not null
                && security.Extra!.Children.ContainsKey(new YamlScalarNode(key));

            Assert.True(
                boundToAMember || boundToTheBucket,
                $"'{key}' is declared by $defs/security and reaches NEITHER a SecuritySpec member " +
                "NOR the Extra bucket after parsing — it is dropped. The likeliest cause is a " +
                "name in ParseSecurity's BuildExtraNode exclusion list that the method does not " +
                "actually bind: excluding a key it does not read is the accept-and-drop state " +
                "#353 closed, reopened for this one key.");
        }
    }

    /// <summary>
    /// The residual #353's bucket does NOT close, pinned because a comment elsewhere depends on
    /// it: a <c>$defs/security</c> property declared with a NON-SCALAR value is rejected by the
    /// schema and is invisible in the AST — <c>GetScalar</c> yields <see langword="null"/> for it,
    /// and the key is on <c>ParseSecurity</c>'s exclusion list so it never reaches
    /// <see cref="SecuritySpec.Extra"/> either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing for <c>ScenarioRunner</c>'s normal-completion return, whose reachability
    /// argument needs a shape the SCHEMA rejects inside one scenario's <c>security</c> block while
    /// both scenarios' ASTs still serialise identically — otherwise the shared-environment gate
    /// aborts the suite first and the mixed-suite path it defends is never taken.
    /// </para>
    /// <para>
    /// That argument used to name an UNKNOWN key, which #353 closed: an unknown key now lands in
    /// <see cref="SecuritySpec.Extra"/> and the two ASTs diverge. This row is the replacement
    /// shape, and it is measured rather than reasoned about — the comment was rewritten to name
    /// it only after this test passed.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASecurityPropertyWithANonScalarValue_IsSchemaRejectedAndInvisibleInTheAst()
    {
        const string offending = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: 9093
                    caCert: {}
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        // The sibling that declares no caCert at all — the other half of the mixed suite.
        const string benign = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: 9093
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var registry = MinimalRegistry();

        // Half one: the schema REJECTS it, with an error located inside the security block.
        var validation = DocumentValidator.Validate(offending, registry);
        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Errors,
            error => error.InstanceLocation.Contains("/security/", StringComparison.Ordinal));

        // Half two: the AST cannot carry it — neither in the typed member nor in the bucket.
        var offendingSecurity = YamlDocumentParser.Parse(offending)
            .Environment!.Dependencies!["events"].Security;
        var benignSecurity = YamlDocumentParser.Parse(benign)
            .Environment!.Dependencies!["events"].Security;

        Assert.NotNull(offendingSecurity);
        Assert.Null(offendingSecurity!.CaCert);
        Assert.Null(offendingSecurity.Extra);

        // And therefore indistinguishable from the sibling that never declared the key.
        Assert.Equal(benignSecurity, offendingSecurity);
    }

    // ── The accept-and-drop hole, closed mechanically ─────────────────────────────────

    /// <summary>
    /// Every property <c>$defs/security</c> declares must have a matching
    /// <see cref="SecuritySpec"/> member, so a field the SCHEMA accepts cannot be silently
    /// DROPPED by the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, not hypothetical. Between the commit that let the schema accept
    /// <c>clientKeyPassword</c> and the one that made <c>SecuritySpec</c> bind it, this
    /// repository was in an ACCEPT-AND-DROP state — the document validated and the value was
    /// discarded before any consumer could read it — and no test was red. The sibling assertion
    /// above is one-sided by design: it pins <see cref="SecuritySpec"/>'s members against a
    /// hand-maintained array, which notices a member VANISHING but nothing about a schema
    /// property that never gained one. This test ties the two surfaces together and derives the
    /// expected set from the composed schema at run time, so the hole cannot reopen for the next
    /// security field the schema learns.
    /// </para>
    /// <para>
    /// The camelCase-to-PascalCase mapping is applied MECHANICALLY, with no exclusion list:
    /// every key <c>$defs/security</c> declares today maps by upper-casing its first character
    /// (<c>serverArtifacts</c> to <see cref="SecuritySpec.ServerArtifacts"/> included, whose
    /// member happens to be a list of a different record — the mapping is over NAMES, never
    /// types). An exclusion list would recreate exactly the hand-maintained hazard this test
    /// exists to remove, so a key that stops mapping cleanly must be reconciled rather than
    /// exempted.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySecuritySchemaProperty_HasAMatchingSecuritySpecMember()
    {
        var registry = MinimalRegistry();
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var rootObj = JsonNode.Parse(composedJson)!.AsObject();

        var schemaProperties = rootObj["$defs"]!["security"]!.AsObject()["properties"]!.AsObject()
            .Select(property => property.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        // Non-vacuity: were the walk above ever to resolve to an empty or wrong node, a clean
        // sweep over ZERO keys would be indistinguishable from a genuine pass.
        Assert.NotEmpty(schemaProperties);
        Assert.Contains("clientKeyPassword", schemaProperties);

        var members = typeof(SecuritySpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var dropped = schemaProperties
            .Where(key => !members.Contains(ToPascalCase(key)))
            .ToArray();

        Assert.True(
            dropped.Length == 0,
            "$defs/security declares " + string.Join(", ", dropped) + ", for which SecuritySpec " +
            "has no matching member. A document declaring such a field VALIDATES and the value " +
            "is then DROPPED before any consumer can read it — the accept-and-drop state this " +
            "repository was already in once, between the schema accepting 'clientKeyPassword' " +
            "and the model binding it, with nothing red to say so. Add the member as an " +
            "INIT-ONLY property (never a positional parameter — see SecuritySpec's own remarks " +
            "on binary compatibility) and bind it in YamlDocumentParser.ParseSecurity.");

    }

    /// <summary>
    /// The layer BELOW the one above: every property <c>$defs/security</c> declares must be
    /// READ by <c>YamlDocumentParser.ParseSecurity</c>, not merely have a
    /// <see cref="SecuritySpec"/> member to be read into. A document declaring the whole
    /// surface is parsed, and no member of the resulting <see cref="SecuritySpec"/> may be
    /// <see langword="null"/> afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EverySecuritySchemaProperty_HasAMatchingSecuritySpecMember</c> closes schema →
    /// model. It cannot close model → parser: a member can exist while <c>ParseSecurity</c>
    /// never reads its key, and the result is the SAME accept-and-drop outcome one layer down
    /// — the document validates, the member stays <see langword="null"/>, and nothing is red.
    /// Only a round trip through the real parser can see that, so this test performs one.
    /// </para>
    /// <para>
    /// The completeness of the fixture is DERIVED from the schema rather than asserted by
    /// hand: every key <c>$defs/security</c> declares must appear as a declared key in the
    /// document below, so a schema property added without a fixture line fails here before it
    /// can reach the null check. The fixture itself is hand-written because each key needs a
    /// type-appropriate value (a port, a path, a whole <c>${secret:}</c> reference, a nested
    /// artefact list), which no mechanical generator produces — and it is first put through
    /// the real <see cref="DocumentValidator"/>, so an illegal fixture fails as an illegal
    /// fixture rather than silently proving nothing.
    /// </para>
    /// <para>
    /// STRUCTURAL ASSUMPTION, recorded because nothing enforces it: a SINGLE fixture requires
    /// every <c>$defs/security</c> property to be legal SIMULTANEOUSLY, which holds only because
    /// <c>mtls</c> admits the whole surface today. If a future field is legal only under one
    /// profile — a <c>tls</c>-only field, say — no one document can declare them all, and this
    /// test must then be split into per-profile fixtures, each parsed and swept as below, with
    /// the UNION of their declared keys asserted against the schema's property list.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySecuritySchemaProperty_IsReadBackByTheParser_NoMemberIsLeftNull()
    {
        // Every $defs/security key, with a type-appropriate value. 'profile: mtls' because it
        // is the only profile under which the whole surface is legal at once (the 'tls' branch
        // forbids clientCert/clientKey/clientKeyPassword), and a kafka dependency because that
        // is the only dependency kind accepting a security block at all.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    caCert: ./certs/ca.pem
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.enc.pem
                    clientKeyPassword: "${secret:env/CLIENT_KEY_PASS}"
                    serverArtifacts:
                      - source: ./certs/broker.jks
                        target: /etc/kafka/secrets/broker.jks
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var registry = MinimalRegistry();

        // Non-vacuity, first: a fixture the schema rejects would prove nothing about what the
        // parser reads back from a LEGAL document.
        var validation = DocumentValidator.Validate(yaml, registry);
        Assert.True(
            validation.IsValid,
            "The whole-surface fixture below must itself be a valid document. Errors: " +
            string.Join("; ", validation.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}")));

        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var schemaProperties = JsonNode.Parse(composedJson)!.AsObject()["$defs"]!["security"]!
            .AsObject()["properties"]!.AsObject()
            .Select(property => property.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(schemaProperties);

        // Derivation, not a hand-written list: the fixture must DECLARE every schema key.
        // Scoped to the 'security:' BLOCK (its own line excluded, everything indented under it
        // included) rather than to the whole document (NIT-E, peer review): a future
        // $defs/security property whose name is already used elsewhere in this fixture — 'type',
        // declared on the dependency two lines above, is the live example — would otherwise be
        // counted as declared by that unrelated line. The null sweep below would still redden,
        // but its message accuses ParseSecurity of not reading a key when the real fault is a
        // fixture missing a line. Matched on a line's own leading token so 'clientKey' is not
        // satisfied by the 'clientKeyPassword' line that merely starts with the same characters.
        var lines = yaml.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var securityLineIndex = Array.FindIndex(lines, line => line.TrimStart() == "security:");
        Assert.True(
            securityLineIndex >= 0,
            "The whole-surface fixture in this test must declare a 'security:' block — the key " +
            "sweep below is scoped to it, and finding none would sweep nothing.");

        var securityIndent = lines[securityLineIndex].Length -
                             lines[securityLineIndex].TrimStart().Length;

        var declaredKeys = lines
            .Skip(securityLineIndex + 1)
            .TakeWhile(line => line.Trim().Length == 0 ||
                               line.Length - line.TrimStart().Length > securityIndent)
            .Select(line => line.Trim())
            .Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line)
            .Select(line => line.IndexOf(':', StringComparison.Ordinal) is var colon && colon > 0
                ? line[..colon]
                : string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        var undeclared = schemaProperties.Where(key => !declaredKeys.Contains(key)).ToArray();
        Assert.True(
            undeclared.Length == 0,
            "$defs/security declares " + string.Join(", ", undeclared) + ", which the " +
            "whole-surface fixture in this test does not. Add the key (with a type-appropriate " +
            "value) — otherwise this test cannot tell whether ParseSecurity reads it.");

        var document = YamlDocumentParser.Parse(yaml);
        var security = document.Environment?.Dependencies?["events"].Security;

        Assert.NotNull(security);

        // Swept over the members the SCHEMA declares, derived by the same mechanical
        // camelCase-to-PascalCase map the sibling test above uses — never a hand-written
        // exclusion list. That scoping is what this test's own subject requires: it asks whether
        // ParseSecurity READS every schema property, and a member no schema property maps to has
        // no key for the fixture to declare. SecuritySpec.Extra (#353) is the live case — the
        // bucket exists precisely for keys $defs/security does NOT declare, so on a schema-VALID
        // fixture it is null by construction, and the fixture must stay schema-valid for the
        // non-vacuity check above to mean anything.
        var schemaMembers = schemaProperties.Select(ToPascalCase).ToHashSet(StringComparer.Ordinal);

        var unread = typeof(SecuritySpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => schemaMembers.Contains(property.Name))
            .Where(property => property.GetValue(security) is null)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Non-vacuity: an empty or mis-derived schemaMembers would sweep nothing and pass.
        Assert.Equal(schemaProperties.Length, schemaMembers.Count);
        Assert.Contains("ClientKeyPassword", schemaMembers);

        // And every mapped name must be a REAL member, or it drops out of the sweep above
        // silently and the accept-and-drop hole reopens GREEN (#353, review round two). The two
        // guards above prove the map is injective and that one name maps; neither proves that a
        // future $defs/security property whose member is not spelled
        // char.ToUpperInvariant(k[0]) + k[1..] is caught rather than skipped.
        var declaredMemberNames = typeof(SecuritySpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            schemaMembers,
            member => Assert.True(
                declaredMemberNames.Contains(member),
                "$defs/security declares a property mapping to SecuritySpec." + member + ", which "
                + "is not a member of that record. The sweep below is scoped to mapped names, so "
                + "this one would be SKIPPED rather than reported — reconcile the name (or the "
                + "map) rather than exempting it."));

        Assert.True(
            unread.Length == 0,
            "SecuritySpec." + string.Join(", SecuritySpec.", unread) + " is null after parsing " +
            "a document that DECLARES every $defs/security property. The member exists and the " +
            "schema accepts the field, but YamlDocumentParser.ParseSecurity never reads its key " +
            "— the accept-and-drop hole one layer below the schema/model check above. Bind the " +
            "key in ParseSecurity.");
    }

    /// <summary>
    /// The camelCase-to-PascalCase map from a <c>$defs/security</c> property name to its
    /// <see cref="SecuritySpec"/> member name.
    /// </summary>
    /// <remarks>
    /// ONE map, shared by the two tests that use it, because they are two halves of one claim
    /// (schema → member exists; member → parser reads it) and a divergence between two copies
    /// would surface as the second test accusing <c>ParseSecurity</c> of not reading a key that
    /// the first had already mapped differently. It was two identical local functions until
    /// #353's review round two.
    /// </remarks>
    private static string ToPascalCase(string camelCase) =>
        char.ToUpperInvariant(camelCase[0]) + camelCase[1..];

    /// <summary>
    /// <see cref="SecuritySpec"/>'s members, ordinally sorted: the six keys
    /// <c>YamlDocumentParser.ParseSecurity</c> binds positionally, plus the two init-only
    /// properties — <c>ClientKeyPassword</c> (client-key-password spec, REQ-003) and
    /// <c>Extra</c>, the untyped bucket for every <c>security</c> key no member above claims
    /// (#353).
    /// </summary>
    private static readonly string[] s_securitySpecMembers =
    {
        "CaCert", "ClientCert", "ClientKey", "ClientKeyPassword", "Endpoint", "Extra", "Profile",
        "ServerArtifacts",
    };
}
