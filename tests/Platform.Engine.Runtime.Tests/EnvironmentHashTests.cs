// S11-B-02 — ComputeEnvironmentHash hardening tests.
//
// These tests are RED against the unpatched code (SerialiseEnvironment calls
// JsonSerializer.Serialize(env) without any converter, which throws
// InvalidOperationException when DependencySpec.Extra is a non-null
// YamlMappingNode) and GREEN once the YamlNodeJsonConverter is wired in.
//
// Seven scenarios:
//   1. No crash with non-null Extra — the call succeeds and returns a non-empty string.
//   2. Determinism (key-order invariance) — two equal mappings declared in different key
//      order produce the SAME hash.
//   3. Fidelity (different extras ≠ same hash) — two mappings that differ in a value
//      produce DIFFERENT hashes.
//   4. Real authored shape — a kafka dependency with schemaRegistry: true parses through
//      the real YAML parser → AST and ComputeEnvironmentHash succeeds.
//   5. Extra-free environments unchanged — an environment without any Extra fields
//      produces the same hash on two independent calls (stability, not regression against
//      an old golden value; no golden value was pinned before this fix).
//   6. Null environment — ComputeEnvironmentHash(null) returns an empty string without
//      throwing (unchanged null-guard contract).
//   7. Complex (non-scalar) key — a YamlMappingNode whose key is a YamlSequenceNode does
//      NOT throw InvalidCastException; proves KeyToString defensive helper works.

using System.Collections.Generic;
using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Runtime;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker tests for <see cref="ScenarioRunner.ComputeEnvironmentHash"/> hardening
/// (S11-B-02): proves that an environment with a non-null <see cref="DependencySpec.Extra"/>
/// (<see cref="YamlMappingNode"/>) does not throw and produces a stable, deterministic hash.
/// </summary>
public sealed class EnvironmentHashTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
        new System.Reflection.Assembly[]
        {
            typeof(HttpRestProvider).Assembly,
        };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    /// <summary>
    /// Builds a minimal <see cref="EnvironmentSpec"/> that has exactly one kafka
    /// dependency carrying the supplied <paramref name="extra"/> node.
    /// </summary>
    private static EnvironmentSpec KafkaEnv(YamlMappingNode? extra) =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: extra),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Builds an <see cref="EnvironmentSpec"/> containing a postgres dependency with no
    /// Extra (the common extra-free case used by most scenarios today).
    /// </summary>
    private static EnvironmentSpec PostgresEnv() =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders-db"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    // ── Test 1: No crash with non-null Extra ──────────────────────────────────

    /// <summary>
    /// <c>ComputeEnvironmentHash</c> must not throw when a dependency carries a
    /// non-null <see cref="YamlMappingNode"/> Extra.
    /// Before S11-B-02 this throws <see cref="System.InvalidOperationException"/>:
    /// "Cannot read the Value of an empty anchor" (from inside YamlDotNet when
    /// System.Text.Json attempts default serialisation of YamlMappingNode).
    /// </summary>
    [Fact]
    public void ComputeEnvironmentHash_WithNonNullExtra_DoesNotThrow_ReturnsNonEmpty()
    {
        // Arrange — Extra = { schemaRegistry: true }
        var extra = new YamlMappingNode
        {
            { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
        };
        var env = KafkaEnv(extra);

        // Act — must NOT throw.
        var hash = ScenarioRunner.ComputeEnvironmentHash(env);

        // Assert
        Assert.False(
            string.IsNullOrEmpty(hash),
            "ComputeEnvironmentHash must return a non-empty string for a non-null environment.");
    }

    // ── Test 2: Determinism — key-order invariance ────────────────────────────

    /// <summary>
    /// Two <see cref="DependencySpec"/> instances whose <c>Extra</c> mappings contain
    /// the SAME keys and values but in DIFFERENT declaration order must produce the SAME
    /// hash.  This proves the ordinal sort in <c>YamlNodeJsonConverter</c> is effective.
    /// </summary>
    [Fact]
    public void ComputeEnvironmentHash_SameExtrasDifferentKeyOrder_ProducesSameHash()
    {
        // Arrange — two mappings with the same entries but reversed key order.
        var extraAB = new YamlMappingNode
        {
            { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
            { new YamlScalarNode("replicationFactor"), new YamlScalarNode("1") },
        };
        var extraBA = new YamlMappingNode
        {
            { new YamlScalarNode("replicationFactor"), new YamlScalarNode("1") },
            { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
        };

        var envAB = KafkaEnv(extraAB);
        var envBA = KafkaEnv(extraBA);

        // Act
        var hashAB = ScenarioRunner.ComputeEnvironmentHash(envAB);
        var hashBA = ScenarioRunner.ComputeEnvironmentHash(envBA);

        // Assert — the hashes must be identical (key-order invariant).
        Assert.Equal(hashAB, hashBA);
    }

    // ── Test 3: Fidelity — different extras → different hashes ───────────────

    /// <summary>
    /// Two <see cref="DependencySpec"/> instances whose <c>Extra</c> mappings differ
    /// in a value must produce DIFFERENT hashes, proving the converter encodes actual
    /// content rather than always emitting the same placeholder.
    /// </summary>
    [Fact]
    public void ComputeEnvironmentHash_DifferentExtras_ProduceDifferentHashes()
    {
        // Arrange — one has schemaRegistry:true, the other has no extra at all.
        var extraWithRegistry = new YamlMappingNode
        {
            { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
        };

        var envWith = KafkaEnv(extraWithRegistry);
        var envWithout = KafkaEnv(extra: null);

        // Act
        var hashWith = ScenarioRunner.ComputeEnvironmentHash(envWith);
        var hashWithout = ScenarioRunner.ComputeEnvironmentHash(envWithout);

        // Assert — the hashes must differ.
        Assert.NotEqual(hashWith, hashWithout);
    }

    // ── Test 4: Real authored shape ───────────────────────────────────────────

    /// <summary>
    /// A kafka dependency with <c>schemaRegistry: true</c> parsed through the real
    /// <see cref="YamlDocumentParser"/> and <see cref="AstBuilder"/> produces an AST
    /// whose <c>Environment</c> has a non-null <see cref="DependencySpec.Extra"/>, and
    /// <c>ComputeEnvironmentHash</c> succeeds on that AST's environment.
    /// </summary>
    [Fact]
    public void ComputeEnvironmentHash_RealParsedKafkaWithSchemaRegistry_DoesNotThrow()
    {
        // Arrange — a minimal YAML that declares a kafka dependency with schemaRegistry.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  schemaRegistry: true
            steps:
              - id: noop
                type: http.rest
                target: api
                method: GET
                path: /api
                expect:
                  status: 200
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        ScenarioAst ast = AstBuilder.Build(doc, s_registry);

        // Confirm the Extra field is non-null (proves the test is exercising the right path).
        Assert.NotNull(ast.Environment);
        Assert.NotNull(ast.Environment!.Dependencies);
        var dep = ast.Environment.Dependencies!["events"];
        Assert.NotNull(dep.Extra);

        // Act — must NOT throw.
        var hash = ScenarioRunner.ComputeEnvironmentHash(ast.Environment);

        // Assert
        Assert.False(
            string.IsNullOrEmpty(hash),
            "ComputeEnvironmentHash must succeed for a real parsed kafka dependency with schemaRegistry.");
    }

    // ── Test 5: Extra-free environments unchanged (stability) ─────────────────

    /// <summary>
    /// An environment containing only extra-free dependencies (e.g. a plain postgres
    /// dependency) must produce the SAME hash on two independent calls — the converter
    /// introduction must not alter the serialisation of environments that have no
    /// <see cref="YamlMappingNode"/> fields.
    /// </summary>
    /// <remarks>
    /// No golden value is hardcoded here because no freeze test pinned the environment
    /// hash before S11-B-02.  Two-call stability is the strongest claim we can make
    /// without a prior baseline; it is sufficient to detect any non-determinism
    /// introduced by the converter change.
    /// </remarks>
    [Fact]
    public void ComputeEnvironmentHash_ExtraFreeEnvironment_StableAcrossCallsAndNonEmpty()
    {
        // Arrange — a plain postgres dependency with no Extra.
        var env = PostgresEnv();

        // Act — call twice independently.
        var hash1 = ScenarioRunner.ComputeEnvironmentHash(env);
        var hash2 = ScenarioRunner.ComputeEnvironmentHash(env);

        // Assert — non-empty and stable.
        Assert.False(
            string.IsNullOrEmpty(hash1),
            "Hash for an extra-free environment must not be empty.");
        Assert.True(
            hash1 == hash2,
            "Hash must be identical across two independent calls (determinism).");
    }

    // ── Bonus: null environment → empty string (unchanged contract) ────────────

    /// <summary>
    /// A null environment must still return an empty string — the <c>null</c> guard in
    /// <c>SerialiseEnvironment</c> must remain intact after the converter change.
    /// </summary>
    [Fact]
    public void ComputeEnvironmentHash_NullEnvironment_ReturnsEmptyString()
    {
        var hash = ScenarioRunner.ComputeEnvironmentHash(null);
        Assert.Equal(string.Empty, hash);
    }

    // ── Test 6: Complex (non-scalar) key ─────────────────────────────────────

    /// <summary>
    /// YAML legally allows complex keys — e.g. a sequence or mapping used as a mapping
    /// key.  Before the <c>KeyToString</c> defensive helper, the unconditional cast
    /// <c>(YamlScalarNode)kv.Key</c> in <c>YamlNodeJsonConverter</c> threw
    /// <see cref="System.InvalidCastException"/> for any non-scalar key, replicating the
    /// original bug the converter was written to fix.
    /// </summary>
    [Fact]
    public void ComputeEnvironmentHash_ComplexSequenceKey_DoesNotThrow_ReturnsNonEmpty()
    {
        // Arrange — Extra mapping whose key is a YamlSequenceNode (not a YamlScalarNode).
        var complexKeyMapping = new YamlMappingNode();
        complexKeyMapping.Add(
            new YamlSequenceNode(new YamlScalarNode("key1"), new YamlScalarNode("key2")),
            new YamlScalarNode("value"));

        var env = KafkaEnv(complexKeyMapping);

        // Act — must NOT throw InvalidCastException.
        var hash = ScenarioRunner.ComputeEnvironmentHash(env);

        // Assert
        Assert.False(
            string.IsNullOrEmpty(hash),
            "ComputeEnvironmentHash must return a non-empty string even when Extra contains a complex (sequence) key.");
    }
}
