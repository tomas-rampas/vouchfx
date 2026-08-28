// S11-B-02 — ComputeEnvironmentHash hardening tests.
//
// These tests are RED against the unpatched code (SerialiseEnvironment calls
// JsonSerializer.Serialize(env) without any converter, which throws
// InvalidOperationException when DependencySpec.Extra is a non-null
// YamlMappingNode) and GREEN once the YamlNodeJsonConverter is wired in.
//
// Eight scenarios (the eighth, 2b, added by dependency-env REQ-001):
//   1. No crash with non-null Extra — the call succeeds and returns a non-empty string.
//   2. Determinism (key-order invariance) — two equal Extra mappings declared in different key
//      order produce the SAME hash.
//  2b. Its counterpart, and the contrast is the point — the TYPED DependencySpec.Env, being a
//      dictionary serialised in enumeration order rather than a YamlMappingNode normalised by
//      YamlNodeJsonConverter, is key-order SENSITIVE: the same two entries in the opposite
//      order produce DIFFERENT hashes.
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
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

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
    /// Builds a minimal <see cref="EnvironmentSpec"/> with one kafka dependency carrying a
    /// TYPED <see cref="DependencySpec.Env"/> whose entries are inserted in the order given.
    /// Insertion order is what matters here: a <see cref="Dictionary{TKey,TValue}"/> built by
    /// additions alone IS OBSERVED TO enumerate in insertion order, and that enumeration order
    /// is precisely what <see cref="System.Text.Json.JsonSerializer"/> writes. "Observed", not
    /// "guaranteed": Microsoft documents <c>Dictionary</c>'s enumeration order as unspecified,
    /// so this is a property of the runtime under test rather than of the contract. The pin is
    /// worth having either way — a runtime that changed it would turn this test red, which is
    /// the notification wanted.
    /// </summary>
    private static EnvironmentSpec KafkaEnvWithTypedEnv(params (string Key, string Value)[] entries)
    {
        var env = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            env[key] = value;
        }

        return new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                {
                    Env = env,
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);
    }

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

    /// <summary>
    /// #353 put a second <see cref="YamlMappingNode"/> on this path —
    /// <see cref="SecuritySpec.Extra"/> — reached through a dependency's <c>security</c> block
    /// rather than through <see cref="DependencySpec.Extra"/>. Two environments differing ONLY in
    /// that bucket must produce different hashes: the bucket has to survive serialisation, and
    /// its content has to reach the digest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing on the WATCH path specifically. <c>WatchRunner</c> computes this hash
    /// immediately after <c>YamlDocumentParser.Parse</c> and BEFORE any schema validation, and
    /// the parser is lenient where the schema is closed — so a document carrying an unbound
    /// <c>security</c> key reaches this serialiser even though the schema would refuse it. A
    /// throw here would surface as a parse failure on a document the engine was about to reject
    /// for an unrelated and correctly-stated reason.
    /// </para>
    /// <para>
    /// <strong>Why this is measured rather than inferred from the sibling row above, corrected in
    /// #353's review round two.</strong> It is NOT that converter resolution differs per property
    /// — measured, it does not: <c>YamlNodeJsonConverter</c> is registered in <c>Converters</c>
    /// and selected by its <c>CanConvert</c> (<c>typeof(YamlNode).IsAssignableFrom</c>), and
    /// both buckets are <c>YamlMappingNode?</c> with no <c>[JsonConverter]</c> attribute, so the
    /// two resolve identically by construction. What is NOT identical by construction is
    /// REACHABILITY: the sibling row proves a node held directly by a dependency survives, and
    /// says nothing about one held two records deeper, behind a member the digest's own sibling
    /// (<c>SecuredTargets.IdentityOf</c>) deliberately does not read.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeEnvironmentHash_WithNonNullSecurityExtra_DoesNotThrow_ReturnsNonEmpty()
    {
        var securityExtra = new YamlMappingNode
        {
            { new YamlScalarNode("trustDomain"), new YamlScalarNode("spiffe://acme.example") },
        };

        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                {
                    Security = new SecuritySpec("acme.spiffe", "9093", null, null, null, null)
                    {
                        Extra = securityExtra,
                    },
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // The same environment with an EMPTY bucket, and with none at all: three states that
        // must not collide, which is the reachability claim stated as a comparison rather than
        // as a substring of a plaintext payload (#353, review round two — this method now
        // returns a digest).
        var withOtherContent = KafkaEnvWithSecurityExtra(
            ("trustDomain", "spiffe://other.example"));
        var withoutBucket = KafkaEnvWithSecurityExtra();

        var hash = ScenarioRunner.ComputeEnvironmentHash(env);
        var hashOther = ScenarioRunner.ComputeEnvironmentHash(withOtherContent);
        var hashNone = ScenarioRunner.ComputeEnvironmentHash(withoutBucket);

        Assert.False(string.IsNullOrEmpty(hash));

        // The bucket reaches the digest: a change confined to it moves the value. A serialiser
        // that dropped SecuritySpec.Extra would make all three of these equal.
        Assert.NotEqual(hashNone, hash);
        Assert.NotEqual(hashNone, hashOther);
        Assert.NotEqual(hash, hashOther);
    }

    /// <summary>
    /// A kafka dependency whose <c>security</c> block carries the given #353 bucket entries, or
    /// no bucket at all when none are given.
    /// </summary>
    private static EnvironmentSpec KafkaEnvWithSecurityExtra(
        params (string Key, string Value)[] entries)
    {
        YamlMappingNode? extra = null;

        if (entries.Length > 0)
        {
            extra = new YamlMappingNode();
            foreach (var (key, value) in entries)
            {
                extra.Children.Add(new YamlScalarNode(key), new YamlScalarNode(value));
            }
        }

        return new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                {
                    Security = new SecuritySpec("acme.spiffe", "9093", null, null, null, null)
                    {
                        Extra = extra,
                    },
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);
    }

    /// <summary>
    /// <c>ComputeEnvironmentHash</c> must not RETURN declared secret material. The environment it
    /// serialises carries <see cref="SecuritySpec.ClientKeyPassword"/> and the two untyped
    /// <c>Extra</c> buckets, none of which this engine can prove non-secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row that makes the digest load-bearing (#353, review round two). The three rows above
    /// assert equality properties that hold of the plaintext JSON just as well as of a digest —
    /// measured: with the method reverted to <c>return SerialiseEnvironment(environment);</c> the
    /// whole of this class still passed 9/9. So they pin the CONSISTENCY of the value and say
    /// nothing about what it discloses, and a later reader could revert the digest for
    /// simplicity's sake with nothing red.
    /// </para>
    /// <para>
    /// Three canaries rather than one, in the three members whose contents no consumer may assume
    /// are non-secret. A hex SHA-256 cannot contain any of them; the plaintext contained all
    /// three.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeEnvironmentHash_DoesNotReturnDeclaredSecretMaterial()
    {
        const string passphraseCanary = "P@ssw0rd-LEAK-CANARY";
        const string securityBucketCanary = "SECURITY-EXTRA-LEAK-CANARY";
        const string dependencyBucketCanary = "DEPENDENCY-EXTRA-LEAK-CANARY";

        var dependencyExtra = new YamlMappingNode
        {
            { new YamlScalarNode("vendorSecret"), new YamlScalarNode(dependencyBucketCanary) },
        };
        var securityExtra = new YamlMappingNode
        {
            { new YamlScalarNode("vendorSecret"), new YamlScalarNode(securityBucketCanary) },
        };

        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: dependencyExtra)
                {
                    Security = new SecuritySpec(
                        "mtls", "9093", "./ca.pem", "./client.pem", "./client.key", null)
                    {
                        ClientKeyPassword = passphraseCanary,
                        Extra = securityExtra,
                    },
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var hash = ScenarioRunner.ComputeEnvironmentHash(env);

        Assert.DoesNotContain(passphraseCanary, hash, StringComparison.Ordinal);
        Assert.DoesNotContain(securityBucketCanary, hash, StringComparison.Ordinal);
        Assert.DoesNotContain(dependencyBucketCanary, hash, StringComparison.Ordinal);

        // Non-vacuity, and the shape of the guarantee: an uppercase hex digest, so no author
        // string of any kind can survive into it. An empty or truncated return would satisfy the
        // three assertions above while proving nothing.
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'),
            $"Expected uppercase hex, found '{c}' in: {hash}"));

        // And the canaries DID reach the serialised form — otherwise this row would pass on an
        // environment whose secret-bearing members were dropped before the digest, proving
        // nothing about the digest at all.
        Assert.NotEqual(
            hash,
            ScenarioRunner.ComputeEnvironmentHash(
                new EnvironmentSpec(
                    Services: null,
                    Dependencies: new Dictionary<string, DependencySpec>
                    {
                        ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                        {
                            Security = new SecuritySpec(
                                "mtls", "9093", "./ca.pem", "./client.pem", "./client.key", null),
                        },
                    },
                    Seed: null,
                    ImageRegistry: null,
                    ImagePullPolicy: null)));
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

    // ── Test 2b: The typed Env is the OPPOSITE — key-order SENSITIVE ─────────

    /// <summary>
    /// The deliberate counterpart to the test directly above, and the reason the two sit
    /// together: <see cref="DependencySpec.Extra"/> is key-order INVARIANT while
    /// <see cref="DependencySpec.Env"/> is key-order SENSITIVE. Which property applies to
    /// which field follows from HOW each is serialised, not from a choice made per test:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>Extra</c> is a <see cref="YamlMappingNode"/> written by
    ///     <c>YamlNodeJsonConverter</c>, which sorts every mapping's keys ordinally — hence
    ///     invariant, as <c>..._SameExtrasDifferentKeyOrder_ProducesSameHash</c> asserts.
    ///   </description></item>
    ///   <item><description>
    ///     <c>Env</c> is an <see cref="IReadOnlyDictionary{TKey,TValue}"/> written by
    ///     <see cref="System.Text.Json.JsonSerializer"/> in enumeration — i.e. declaration —
    ///     order, which no converter normalises; hence sensitive.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// PINS THE BEHAVIOUR THAT EXISTS, AND IT IS THE INTENDED ONE. Moving <c>env</c> out of
    /// <c>Extra</c> into a typed field (dependency-env REQ-001) moved this property with it.
    /// <see cref="ServiceSpec.Env"/> is the same shape, so a SERVICE <c>env:</c> has been
    /// key-order sensitive since #159; a dependency <c>env:</c> now matches it rather than
    /// being the one env map in the language with different equality semantics.
    /// </para>
    /// <para>
    /// It is not a hash-only detail: <c>RunSuiteAsync</c> compares this exact string
    /// ordinally to validate the shared-environment assumption, ABOVE the per-scenario
    /// <c>DocumentValidator.Validate</c> call, so two scenarios whose dependency <c>env:</c>
    /// differs only in key order abort the whole suite as EnvironmentError, and
    /// <c>WatchRunner</c> restarts the topology on a save that merely reorders two keys.
    /// Changing that is a deliberate decision to be taken with this test in front of it —
    /// which is why the test exists.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeEnvironmentHash_SameTypedEnvDifferentKeyOrder_ProducesDifferentHash()
    {
        // Arrange — the same two entries, declared in opposite order. Neither name is one a
        // provider or the engine sets; they exist only to be distinguishable when sorted.
        var envAB = KafkaEnvWithTypedEnv(("A", "1"), ("B", "2"));
        var envBA = KafkaEnvWithTypedEnv(("B", "2"), ("A", "1"));

        // Act
        var hashAB = ScenarioRunner.ComputeEnvironmentHash(envAB);
        var hashBA = ScenarioRunner.ComputeEnvironmentHash(envBA);

        // Assert — different, and the message carries both so a future flip is self-explaining.
        Assert.False(
            hashAB == hashBA,
            "DependencySpec.Env is expected to be key-order SENSITIVE (System.Text.Json writes a "
                + "dictionary in enumeration order and no converter normalises it), unlike "
                + $"DependencySpec.Extra.{System.Environment.NewLine}AB = {hashAB}"
                + $"{System.Environment.NewLine}BA = {hashBA}");

        // And the difference is exactly the key ORDER, not lost content — the alternative
        // explanation this excludes is "the serialiser dropped the second entry of each map",
        // which would leave the inequality above intact. A one-entry environment is the control:
        // were the second entry being dropped, AB and BA would each equal it.
        //
        // Asserted by COMPARISON rather than by looking for '"A":"1"' inside the returned value,
        // which is what this test did while ComputeEnvironmentHash returned its plaintext JSON
        // (#353, review round two). The property under test is unchanged; only a digest cannot be
        // read for substrings, and a method named for a hash should never have been readable that
        // way in the first place.
        var hashAOnly = ScenarioRunner.ComputeEnvironmentHash(KafkaEnvWithTypedEnv(("A", "1")));

        Assert.NotEqual(hashAOnly, hashAB);
        Assert.NotEqual(hashAOnly, hashBA);
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
