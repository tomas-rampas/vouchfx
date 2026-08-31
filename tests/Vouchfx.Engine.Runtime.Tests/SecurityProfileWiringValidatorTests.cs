// SecurityProfileWiringValidator tests (authenticated-infrastructure-mtls, slice C — REQ-022).
//
// REQ-022's invariant: for every declared 'security' block, the (profile, target-kind) pair
// must resolve to a registered wiring, checked at validation time; a pair with no wiring fails
// the suite naming both halves. This file proves the RED-FIRST half of that acceptance
// criterion directly: a suite declaring a (profile, kind) pair that WOULD resolve against the
// full built-in registry stops resolving the moment that wiring is removed — the exact
// "removing a wiring must make a suite declaring that pair fail" proof REQ-022's own acceptance
// text names, exercised both at the validator level and end-to-end through
// ProviderPipeline.Compile.
//
// m1 (peer review, fix round 2): this header used to end "…unlike ProviderPipeline.Compile,
// which always uses the production SecurityProfileRegistry.BuiltIn". That stopped being true in
// the same commit that wrote it — G-MINOR-8 gave Compile an injectable registry, and line ~350
// of this very file passes a reduced one to it. The injection change landed without updating the
// sentence it invalidated.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class SecurityProfileWiringValidatorTests : System.IDisposable
{
    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

    // Only the end-to-end ProviderPipeline.Compile test needs real files on disk (it runs
    // AFTER EnvironmentSecurityValidator's own containment/existence preflight, which would
    // otherwise short-circuit before this test ever reaches SecurityProfileWiringValidator's
    // own check) — every other test in this file calls SecurityProfileWiringValidator.Validate
    // directly and needs no filesystem state at all.
    private readonly string _suiteDir =
        Path.Combine(Path.GetTempPath(), "vouchfx-security-wiring-tests-" + System.Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_suiteDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    private void WriteSuiteFile(string relativePath)
    {
        var full = Path.Combine(_suiteDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "placeholder");
    }

    private static ScenarioAst BuildAst(string yaml)
    {
        var doc = YamlDocumentParser.Parse(yaml);
        return AstBuilder.Build(doc, s_registry);
    }

    // ── No security declared: a no-op ──────────────────────────────────────────

    [Fact]
    public void Validate_NoSecurityDeclared_ReturnsNull()
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        var result = SecurityProfileWiringValidator.Validate(ast, SecurityProfileRegistry.BuiltIn);

        Assert.Null(result);
    }

    // ── The full built-in registry resolves every currently-wired pair ─────────

    [Fact]
    public void Validate_KafkaMtls_AgainstFullRegistry_ReturnsNull()
    {
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        var result = SecurityProfileWiringValidator.Validate(ast, SecurityProfileRegistry.BuiltIn);

        Assert.Null(result);
    }

    [Fact]
    public void Validate_ServiceMtls_AgainstFullRegistry_ReturnsNull()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        var result = SecurityProfileWiringValidator.Validate(ast, SecurityProfileRegistry.BuiltIn);

        Assert.Null(result);
    }

    // ── RED-FIRST: removing a wiring makes its own pair fail, naming both halves ──

    /// <summary>
    /// The exact acceptance-criterion proof: 'kafka'+'mtls' resolves against the FULL
    /// built-in registry (proven by <see cref="Validate_KafkaMtls_AgainstFullRegistry_ReturnsNull"/>
    /// immediately above), then fails the moment the 'mtls' wiring is removed — a registry
    /// built from <see cref="SecurityProfileRegistry.BuiltIn"/>'s own remaining wirings, never
    /// a hand-authored stand-in, so the removal is proven against the real production set.
    /// </summary>
    [Fact]
    public void Validate_KafkaMtls_AfterRemovingMtlsWiring_FailsNamingProfileAndKind()
    {
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        var reducedRegistry = SecurityProfileRegistry.BuildAndFreeze(
            SecurityProfileRegistry.BuiltIn.All
                .Where(w => w.Profile != "mtls")
                .Select(w => w.Instance)
                .ToList());

        var result = SecurityProfileWiringValidator.Validate(ast, reducedRegistry);

        Assert.NotNull(result);
        Assert.Contains("mtls", result!.Message, System.StringComparison.Ordinal);
        Assert.Contains("kafka", result.Message, System.StringComparison.Ordinal);
        Assert.True(result.IsSecurityPreflight);
    }

    /// <summary>
    /// The service-side sibling: removing 'mtls' also fails a service declaring it, again
    /// naming both halves.
    /// </summary>
    [Fact]
    public void Validate_ServiceMtls_AfterRemovingMtlsWiring_FailsNamingProfileAndKind()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        var reducedRegistry = SecurityProfileRegistry.BuildAndFreeze(
            SecurityProfileRegistry.BuiltIn.All
                .Where(w => w.Profile != "mtls")
                .Select(w => w.Instance)
                .ToList());

        var result = SecurityProfileWiringValidator.Validate(ast, reducedRegistry);

        Assert.NotNull(result);
        Assert.Contains("mtls", result!.Message, System.StringComparison.Ordinal);
        // G-MINOR-4 (gatekeeper): a bare "service" substring is ALREADY satisfied by
        // "environment.services.app" earlier in the same message — it does not prove the
        // TARGET KIND itself was rendered. Assert the exact "target kind 'service'" tail
        // instead, naming the specific field the message's own format string produces.
        Assert.Contains("target kind 'service'", result.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing EVERY wiring (the empty registry) fails even 'tls', the least demanding profile
    /// — the invariant degrades to "nothing is wired", never to "everything passes".
    /// </summary>
    /// <remarks>
    /// Declared on a KAFKA dependency (M1, second peer-review round): 'tls' used to be wired for
    /// every target kind unconditionally, so this test previously used redis and its assertion
    /// was strong there. Now that no profile is wired for a non-kafka dependency, redis would
    /// fail against the FULL registry too — leaving the test green while proving nothing about
    /// the empty one. The precondition below closes that gap explicitly rather than relying on
    /// the reader to notice it.
    /// </remarks>
    [Fact]
    public void Validate_Tls_AgainstEmptyRegistry_Fails()
    {
        const string yaml = """
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

        var ast = BuildAst(yaml);

        Assert.Null(SecurityProfileWiringValidator.Validate(ast, SecurityProfileRegistry.BuiltIn));

        var emptyRegistry = SecurityProfileRegistry.BuildAndFreeze(System.Array.Empty<ISecurityProfileWiring>());

        var result = SecurityProfileWiringValidator.Validate(ast, emptyRegistry);

        Assert.NotNull(result);
        Assert.Contains("tls", result!.Message, System.StringComparison.Ordinal);
        Assert.Contains("kafka", result.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// M1 (peer review, fix round 2): 'tls' on a non-kafka dependency no longer resolves against
    /// the PRODUCTION registry either. This is the registry-side half of the schema tightening —
    /// both gates must agree, or a suite could pass one and fail the other. The pipeline-level
    /// sibling for 'mtls' is
    /// <see cref="Compile_MtlsOnNonKafkaDependency_ReturnsPipelineFailure_NamingProfileAndKind"/>.
    /// </summary>
    [Fact]
    public void Validate_TlsOnNonKafkaDependency_AgainstFullRegistry_Fails()
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: tls
                    endpoint: 6380
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        var result = SecurityProfileWiringValidator.Validate(ast, SecurityProfileRegistry.BuiltIn);

        Assert.NotNull(result);
        Assert.Contains("tls", result!.Message, System.StringComparison.Ordinal);
        Assert.Contains("target kind 'redis'", result.Message, System.StringComparison.Ordinal);
        Assert.True(result.IsSecurityPreflight);
    }

    /// <summary>
    /// SEC-1 (peer review, fix round 3): the failure message bounds the author-controlled
    /// <c>profile</c> value at the same 200-character limit
    /// <see cref="SecurityProfileRegistry.DescribeUnknownProfile"/> applies, through the same
    /// helper.
    /// </summary>
    /// <remarks>
    /// A long value is unreachable through the production call sites TODAY — all four hard-return
    /// on schema failure before <c>ProviderPipeline.Compile</c>, so only <c>tls</c>/<c>mtls</c>
    /// survive Stage 1 — but that is an ORDERING invariant, not a local bound, and this series
    /// already found one ordering assumption of exactly this shape to be false on a reachable
    /// path. This test therefore drives <see cref="SecurityProfileWiringValidator.Validate"/>
    /// DIRECTLY, which is precisely the reachability the bound must not depend on: a test routed
    /// through the schema stage could never construct the input and would prove nothing.
    /// </remarks>
    [Fact]
    public void Validate_OverlongProfileValue_IsBoundedInTheFailureMessage()
    {
        const int bound = SecurityProfileRegistry.MaxOffendingValueChars;
        var longProfile = new string('a', 5000);

        var yaml = $$"""
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: {{longProfile}}
                    endpoint: 9093
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var result = SecurityProfileWiringValidator.Validate(BuildAst(yaml), SecurityProfileRegistry.BuiltIn);

        Assert.NotNull(result);
        Assert.DoesNotContain(longProfile, result!.Message, System.StringComparison.Ordinal);
        Assert.Contains($"{new string('a', bound)}... (5000 chars total)", result.Message,
            System.StringComparison.Ordinal);

        // The message stays O(1) in the offending value: everything past the bound is the fixed
        // surrounding text, never a multiple of the input.
        Assert.True(result.Message.Length < bound + 200,
            $"Message must be O(1) in the offending value's length; was {result.Message.Length} chars.");
    }

    // ── End-to-end: wired into ProviderPipeline.Compile with the PRODUCTION registry ──

    /// <summary>
    /// Proves the invariant actually runs as part of <see cref="ProviderPipeline.Compile"/> —
    /// not merely that the standalone validator behaves correctly in isolation — using a pair
    /// the PRODUCTION <see cref="SecurityProfileRegistry.BuiltIn"/> genuinely does not wire
    /// ('mtls' on a non-kafka dependency, mirroring REQ-021's own schema narrowing): reaches
    /// this check by calling <see cref="ProviderPipeline.Compile"/> directly (bypassing schema
    /// validation, exactly like <c>EnvironmentSecurityValidatorTests</c>' own integration
    /// tests), since the schema itself would already refuse this document before Compile is
    /// ever reached through the normal <c>vouchfx validate</c>/<c>run</c> front door.
    /// </summary>
    [Fact]
    public void Compile_MtlsOnNonKafkaDependency_ReturnsPipelineFailure_NamingProfileAndKind()
    {
        Directory.CreateDirectory(_suiteDir);
        WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");

        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: mtls
                    endpoint: 6380
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        var result = ProviderPipeline.Compile(ast, s_registry, "TestSuite", _suiteDir);

        Assert.Null(result.Assembled);
        Assert.NotNull(result.Failure);
        Assert.Contains("mtls", result.Failure!.Message, System.StringComparison.Ordinal);
        Assert.Contains("redis", result.Failure.Message, System.StringComparison.Ordinal);
        Assert.True(result.Failure.IsSecurityPreflight);
    }

    /// <summary>
    /// G-MINOR-8 (gatekeeper): before this fix, <see cref="ProviderPipeline.Compile"/> always
    /// hardcoded <see cref="SecurityProfileRegistry.BuiltIn"/> — unlike <c>registry</c>
    /// (<see cref="StepKindRegistry"/>), which is always a parameter — so REQ-022's own
    /// red-first proof ("removing a wiring must make a suite declaring that pair fail") could
    /// never run END-TO-END through the front door a real caller uses; only the standalone
    /// <see cref="SecurityProfileWiringValidator.Validate"/> call could be driven with a
    /// reduced registry (see the tests above). Now that <c>Compile</c> accepts an optional
    /// <c>securityProfileRegistry</c> parameter, this proves the SAME reduction — 'kafka'+'mtls'
    /// resolves against the full built-in registry, then fails the moment 'mtls' is removed —
    /// through <c>ProviderPipeline.Compile</c> itself, never a hand-authored stand-in registry.
    /// </summary>
    [Fact]
    public void Compile_KafkaMtls_WithInjectedReducedRegistry_FailsEndToEnd_NamingProfileAndKind()
    {
        Directory.CreateDirectory(_suiteDir);
        WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");

        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;

        var ast = BuildAst(yaml);

        // Precondition, proven against the FULL production registry (the default when the
        // new parameter is omitted): this pair resolves, so a failure below is caused by the
        // injected reduction, not by an unrelated defect.
        var precondition = ProviderPipeline.Compile(ast, s_registry, "TestSuite", _suiteDir);
        Assert.Null(precondition.Failure);

        var reducedRegistry = SecurityProfileRegistry.BuildAndFreeze(
            SecurityProfileRegistry.BuiltIn.All
                .Where(w => w.Profile != "mtls")
                .Select(w => w.Instance)
                .ToList());

        var result = ProviderPipeline.Compile(ast, s_registry, "TestSuite", _suiteDir, reducedRegistry);

        Assert.Null(result.Assembled);
        Assert.NotNull(result.Failure);
        Assert.Contains("mtls", result.Failure!.Message, System.StringComparison.Ordinal);
        Assert.Contains("kafka", result.Failure.Message, System.StringComparison.Ordinal);
        Assert.True(result.Failure.IsSecurityPreflight);
    }
}
