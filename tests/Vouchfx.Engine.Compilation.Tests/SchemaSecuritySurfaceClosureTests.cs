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
// And one thing it records rather than proves (m4, second peer-review round): the seam stops
// AT THE SCHEMA. Finding 1 shows a composed profile fragment's own field validating; nothing
// downstream can then read it, because YamlDocumentParser.ParseSecurity binds a fixed set of
// keys into SecuritySpec, which carries no Extra bucket (DependencySpec does). Until that additive
// fix lands, REQ-020 buys authoring-time extensibility only — see
// SecuritySpec_HasNoExtraBucket_SoAComposedProfileFieldIsDroppedAfterValidation at the foot of
// this file, which fails the day the bucket appears.
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Vouchfx.Engine.Authoring.Model;
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
    /// <see cref="SchemaErrorCollector.SuppressUnevaluatedPropertiesCascade"/> into existence
    /// for <c>$defs/step</c> — verified empirically (see the class remarks and this file's own
    /// Finding 1/2 above) — but <c>SuppressUnevaluatedPropertiesCascade</c>'s OWN scoping is
    /// deliberately confined to the step surface (<see cref="SchemaErrorCollector.TryGetStepScope"/>
    /// only ever recognises <c>/steps/&lt;N&gt;</c>), so the environment/security surface has NO
    /// equivalent cascade protection today. Nothing currently NEEDS one — each of these four
    /// independent single-field defects happens to produce exactly one error already — but
    /// nothing GUARDS that either, and a JsonSchema.Net upgrade changing same-object
    /// annotation-propagation behaviour (exactly the risk <c>NaiveFix_*</c> above pins for the
    /// sibling-keyword trap) could silently turn one genuine defect into a false "unknown
    /// property" pile-up alongside it, with no test here to notice. Pins single-error output,
    /// through the real <see cref="DocumentValidator"/> front door, for the four shapes
    /// measured clean: a blank <c>caCert</c>, an out-of-range <c>endpoint</c>, a literal
    /// <c>clientKeyPassword</c>, and a nested <c>serverArtifacts[0].target</c> pattern miss.
    /// </summary>
    /// <remarks>
    /// The caCert and endpoint fixtures moved from a redis dependency to a kafka one in M1
    /// (second peer-review round): a 'security' block on any non-kafka dependency kind is now
    /// rejected outright, which would make those documents single-error for a reason that has
    /// nothing to do with an annotation cascade — the guard would still be green while
    /// proving nothing. Kafka accepts the block, so the single error each fixture yields is
    /// still the one direct-field defect it declares. The serverArtifacts fixture was already
    /// on kafka, and the clientKeyPassword fixture was written on kafka for the same reason.
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

    // ── Where the REQ-020 seam STOPS: the schema layer, and no further ────────────────

    /// <summary>
    /// m4 (peer review, fix round 2): REQ-020's composed-profile-fragment seam is proven at the
    /// SCHEMA layer by Finding 1 above — and the NEXT layer silently drops the data.
    /// <c>YamlDocumentParser.ParseSecurity</c> reads a fixed set of keys and binds them into
    /// <see cref="SecuritySpec"/>, which — unlike its sibling
    /// <see cref="Vouchfx.Engine.Authoring.Model.DependencySpec"/> — has no <c>Extra</c> bucket.
    /// So a composed profile fragment's own field validates and is then discarded before any
    /// consumer can see it: today the seam is authoring-time only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is recorded, not fixed. Fixing it is purely additive (an init-only property on an
    /// UNFROZEN Authoring record — <see cref="SecuritySpec"/>'s own remarks already state the
    /// init-only-never-positional rule that makes it safe), so nothing is lost by deferring it
    /// until a second profile exists to define what the bucket should carry. What was NOT
    /// acceptable was leaving the limit unrecorded: every other statement about REQ-020 in this
    /// repository describes the seam as working, with nothing anywhere saying it stops at the
    /// schema.
    /// </para>
    /// <para>
    /// Asserted by REFLECTION over the record's own members rather than by round-tripping a
    /// fragment field, because there is no shipped profile fragment to round-trip: the real
    /// schema rejects an unknown key inside <c>security</c> (that is Finding 1's whole point),
    /// so the only way to observe the drop today is to look at what the model CAN hold. The
    /// <c>DependencySpec</c> half of the assertion is the control — it proves this test can
    /// detect a bucket when one exists, so the <c>SecuritySpec</c> half is not vacuous — and the
    /// test flips the moment the bucket is added, which is exactly when this note needs
    /// rewriting.
    /// </para>
    /// </remarks>
    [Fact]
    public void SecuritySpec_HasNoExtraBucket_SoAComposedProfileFieldIsDroppedAfterValidation()
    {
        static bool HasExtraBucket(Type recordType) =>
            recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p => p.Name == "Extra");

        Assert.True(HasExtraBucket(typeof(Vouchfx.Engine.Authoring.Model.DependencySpec)),
            "Control: DependencySpec is expected to carry an 'Extra' bucket — if it no longer " +
            "does, this test can no longer detect one and the SecuritySpec assertion below " +
            "proves nothing.");

        Assert.False(HasExtraBucket(typeof(SecuritySpec)),
            "SecuritySpec has GAINED an 'Extra' bucket. That is the additive fix this test " +
            "documents as deferred — good news, but it means REQ-020's seam no longer stops at " +
            "the schema, so update this test, SchemaSecuritySurfaceClosureTests' own header, and " +
            "the CHANGELOG entry that records the limit.");

        // The fixed keys ParseSecurity reads, pinned so a further fixed key added without an
        // Extra bucket still leaves this note accurate about the shape it describes.
        var declared = typeof(SecuritySpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(s_securitySpecMembers, declared);
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

        static string ToPascalCase(string camelCase) =>
            char.ToUpperInvariant(camelCase[0]) + camelCase[1..];
    }

    /// <summary>
    /// The fixed set of keys <c>YamlDocumentParser.ParseSecurity</c> binds, ordinally sorted.
    /// <c>ClientKeyPassword</c> joined that set on 2026-08-11 (client-key-password spec,
    /// REQ-003) as an INIT-ONLY property — one more FIXED key, deliberately not an
    /// <c>Extra</c> bucket, so the closure this class documents is unchanged in kind.
    /// </summary>
    private static readonly string[] s_securitySpecMembers =
    {
        "CaCert", "ClientCert", "ClientKey", "ClientKeyPassword", "Endpoint", "Profile",
        "ServerArtifacts",
    };
}
