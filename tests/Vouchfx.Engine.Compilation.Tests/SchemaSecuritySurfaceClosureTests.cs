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
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

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
        // unevaluatedProperties: false.
        const string instance = """
            {
              "environment": {
                "dependencies": {
                  "cache": {
                    "type": "redis",
                    "security": {
                      "profile": "tls",
                      "endpoint": 6380,
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
    /// <see cref="SchemaErrorCollector.SuppressUnevaluatedPropertiesCascade"/> into existence
    /// for <c>$defs/step</c> — verified empirically (see the class remarks and this file's own
    /// Finding 1/2 above) — but <c>SuppressUnevaluatedPropertiesCascade</c>'s OWN scoping is
    /// deliberately confined to the step surface (<see cref="SchemaErrorCollector.TryGetStepScope"/>
    /// only ever recognises <c>/steps/&lt;N&gt;</c>), so the environment/security surface has NO
    /// equivalent cascade protection today. Nothing currently NEEDS one — each of these three
    /// independent single-field defects happens to produce exactly one error already — but
    /// nothing GUARDS that either, and a JsonSchema.Net upgrade changing same-object
    /// annotation-propagation behaviour (exactly the risk <c>NaiveFix_*</c> above pins for the
    /// sibling-keyword trap) could silently turn one genuine defect into a false "unknown
    /// property" pile-up alongside it, with no test here to notice. Pins single-error output,
    /// through the real <see cref="DocumentValidator"/> front door, for the three shapes the
    /// gatekeeper measured clean: a blank <c>caCert</c>, an out-of-range <c>endpoint</c>, and a
    /// nested <c>serverArtifacts[0].target</c> pattern miss.
    /// </summary>
    [Theory]
    [InlineData(
        // A declared-but-blank caCert fails $defs/security.properties.caCert's own
        // 'minLength: 1' — a single, direct-field defect.
        """
        environment:
          dependencies:
            cache:
              type: redis
              security:
                profile: tls
                endpoint: 6380
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
            cache:
              type: redis
              security:
                profile: tls
                endpoint: 70000
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
}
