// Vouchfx.Engine.Runtime.Tests — EnvironmentSecurityValidatorTests (authenticated-
// infrastructure-mtls, PR A).
//
// Non-docker unit tests for EnvironmentSecurityValidator: REQ-003 (path containment),
// REQ-004 (preflight existence for DECLARED paths only — REQ-004(b): an undeclared
// optional field is absent, not missing, and gets no check), and EDGE-006 (containment
// is checked BEFORE existence — a traversal that happens to point at a real file
// elsewhere on the host still fails with the containment error, never a "found"/"not
// found" one). The last section proves the wiring into ProviderPipeline.Compile — the
// SAME pre-topology stage `vouchfx validate` and pre-topology `vouchfx run` both call —
// with a couple of through-the-pipeline cases.
//
// Fixture files are created under a fresh temp directory per test instance (IDisposable
// teardown), mirroring ScriptCsharpProviderTests' own temp-directory idiom (see that
// file's remarks) — the same pattern this PR's EnvironmentSecurityValidator itself
// mirrors from ScriptCsharpProvider.Validate's file-existence check.

using System;
using System.Collections.Generic;
using System.IO;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="EnvironmentSecurityValidator"/>.
/// </summary>
public sealed class EnvironmentSecurityValidatorTests : IDisposable
{
    // The suite directory every test's SecuritySpec paths are declared relative to.
    private readonly string _suiteDir =
        Path.Combine(Path.GetTempPath(), "vouchfx-env-security-tests-" + Guid.NewGuid().ToString("n"));

    // A SIBLING directory, deliberately OUTSIDE _suiteDir's own tree — stands in for
    // "somewhere else on the host" for the containment (REQ-003/EDGE-006) tests.
    private readonly string _outsideDir =
        Path.Combine(Path.GetTempPath(), "vouchfx-env-security-tests-outside-" + Guid.NewGuid().ToString("n"));

    public EnvironmentSecurityValidatorTests()
    {
        Directory.CreateDirectory(_suiteDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        TryDelete(_suiteDir);
        TryDelete(_outsideDir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    // ── Fixture-file helpers ─────────────────────────────────────────────────

    private string WriteSuiteFile(string relativePath)
    {
        var full = Path.Combine(_suiteDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "placeholder");
        return full;
    }

    private string WriteOutsideFile(string relativePath)
    {
        var full = Path.Combine(_outsideDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "placeholder");
        return full;
    }

    /// <summary>Relative traversal string from <see cref="_suiteDir"/> to a real host path.</summary>
    private string TraversalTo(string absolutePath) =>
        Path.GetRelativePath(_suiteDir, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    // ── AST-construction helpers (direct object graph — no YAML/registry needed) ──

    private static ScenarioAst BuildAst(
        IReadOnlyDictionary<string, ServiceSpec>? services = null,
        IReadOnlyDictionary<string, DependencySpec>? dependencies = null) =>
        new(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: services,
                Dependencies: dependencies,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

    private static Dictionary<string, DependencySpec> OneDependency(string name, SecuritySpec security) =>
        new(StringComparer.Ordinal)
        {
            [name] = new DependencySpec("redis", null, null) { Security = security },
        };

    private static Dictionary<string, ServiceSpec> OneService(string name, SecuritySpec security) =>
        new(StringComparer.Ordinal)
        {
            [name] = new ServiceSpec("myorg/app:1.0", null, null, null, null) { Security = security },
        };

    // ── 1. No security block at all ─────────────────────────────────────────

    [Fact]
    public void Validate_NoEnvironment_ReturnsNull()
    {
        var ast = new ScenarioAst(null, null, new Dictionary<string, string>(StringComparer.Ordinal), Array.Empty<StepNode>());

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.Null(result);
    }

    [Fact]
    public void Validate_DependencyWithoutSecurityBlock_ReturnsNull()
    {
        var deps = new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
        {
            ["db"] = new DependencySpec("postgres", null, null),
        };
        var ast = BuildAst(dependencies: deps);

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.Null(result);
    }

    // ── 2. REQ-004(b): an undeclared optional field is absent, not missing ──

    /// <summary>
    /// A suite declaring <c>security: { profile: mtls, endpoint: 9093, clientCert: ...,
    /// clientKey: ... }</c> with NO <c>caCert</c> field at all must pass this preflight
    /// stage cleanly — no error is raised for the absent field (REQ-004 acceptance (b)).
    /// </summary>
    [Fact]
    public void Validate_MtlsWithoutCaCert_DeclaredFilesExist_ReturnsNull()
    {
        WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");
        var security = new SecuritySpec("mtls", "9093", null, "./certs/client.pem", "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.Null(result);
    }

    // ── 3. REQ-004(a): a declared-but-missing file fails, naming the field + DECLARED path ──

    /// <summary>
    /// REQ-004(a), as superseded by BLOCKER B2 (maintainer decision, 2026-08-26).
    /// </summary>
    /// <remarks>
    /// This test used to be <c>…_FailsNamingFieldAndResolvedPath</c> and asserted the resolved
    /// absolute path was PRESENT — the original acceptance criterion, written when a
    /// validation-time diagnostic reached the author's terminal and nothing else. #372/#407 carried
    /// that text onto <c>ScenarioCompletedEvent.Message</c> and so into the event stream, the JUnit
    /// report and the HTML report, which made the criterion a host-path disclosure. The assertion
    /// is inverted rather than deleted: the absence is now the requirement, and
    /// <c>SecurityDiagnosticPathDisclosureTests</c> pins the same rule end-to-end through the
    /// written artefacts.
    /// </remarks>
    [Fact]
    public void Validate_DeclaredClientCertMissing_FailsNamingFieldAndDeclaredPathOnly()
    {
        var security = new SecuritySpec("mtls", "9093", null, "./certs/client.pem", "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));
        // Neither certificate file is created.

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.mq.security.clientCert", result!.Message, StringComparison.Ordinal);

        // The author's own text — the actionable half — is kept.
        Assert.Contains("./certs/client.pem", result.Message, StringComparison.Ordinal);

        // The base it resolves against is named as a CONCEPT, so a relative path stays diagnosable.
        Assert.Contains("relative to the suite directory", result.Message, StringComparison.Ordinal);

        // …and neither the resolved file nor the resolved suite directory is disclosed.
        var expectedResolved = Path.GetFullPath(Path.Combine(_suiteDir, "certs", "client.pem"));
        Assert.DoesNotContain(expectedResolved, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_suiteDir, result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(result.IsSecurityPreflight);
    }

    [Fact]
    public void Validate_DeclaredCaCertMissing_FailsNamingCaCert()
    {
        var security = new SecuritySpec("tls", "6380", "./certs/ca.pem", null, null, null);
        var ast = BuildAst(dependencies: OneDependency("cache", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.cache.security.caCert", result!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-004 acceptance (a): "creating the file at that path (with any content) makes
    /// validation proceed past this check".
    /// </summary>
    [Fact]
    public void Validate_CreatingTheDeclaredFile_MakesValidationProceedPastTheCheck()
    {
        var security = new SecuritySpec("mtls", "9093", null, "./certs/client.pem", "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var beforeCreate = EnvironmentSecurityValidator.Validate(ast, _suiteDir);
        Assert.NotNull(beforeCreate);

        WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");

        var afterCreate = EnvironmentSecurityValidator.Validate(ast, _suiteDir);
        Assert.Null(afterCreate);
    }

    // ── 4. REQ-003 / EDGE-006: containment checked BEFORE existence ─────────

    /// <summary>
    /// EDGE-006 acceptance: "a suite with security.clientCert: ../../secrets/id_rsa (a
    /// file that happens to exist on the test host outside the suite directory) fails
    /// vouchfx validate with a path-containment error, not a 'file not found' error and
    /// not a pass."
    /// </summary>
    [Fact]
    public void Validate_PathEscapesSuiteDirectory_ButRealFileExistsElsewhere_FailsWithContainmentNotExistence()
    {
        var outsideFile = WriteOutsideFile("id_rsa");
        var traversal = TraversalTo(outsideFile);
        var security = new SecuritySpec("mtls", "9093", null, traversal, "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("resolves outside the suite directory", result!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsSecurityPreflight);
    }

    [Fact]
    public void Validate_PathEscapesSuiteDirectory_AndDoesNotExistAnywhere_StillFailsWithContainmentError()
    {
        var security = new SecuritySpec(
            "mtls", "9093", null, "../../../../nonexistent/client.pem", "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("resolves outside the suite directory", result!.Message, StringComparison.Ordinal);
    }

    // ── 4b. REQ-003: a ROOTED declared path is rejected outright (critic MAJOR-2) ──
    //
    // REQ-003's own wording requires every path-valued security field to be "resolved
    // relative to the directory containing the .e2e.yaml file". Before this fix, a
    // ROOTED declaredPath reached Path.Combine(resolvedSuiteDirectory, declaredPath) —
    // and Path.Combine DISCARDS its first argument outright when the second is rooted
    // (documented .NET behaviour), so the suite directory played no part at all in the
    // resolution. Two consequences that must both be closed by rejecting a rooted path
    // BEFORE Path.Combine is ever called, not merely by chance containment failure:
    //   (a) a rooted path that HAPPENS to resolve inside the suite directory was
    //       silently accepted — REQ-003 says "relative", not "relative, or absolute-
    //       but-it-worked-out"; and
    //   (b) a rooted path that resolves outside was already rejected, but only via
    //       IsContainedWithin's Ordinal comparison — which produces a confusing
    //       "resolves outside" message for a drive-letter-only-casing mismatch (e.g.
    //       declared 'c:\suite\...' against suite directory 'C:\suite'), since the
    //       rooted path's own casing bypasses the suite directory's casing entirely.
    //       Rejecting every rooted path up front closes this whole class, not merely
    //       the one message.

    /// <summary>
    /// The "untested absolute-inside acceptance" the critic flagged: a rooted path that
    /// happens to resolve inside the suite directory must still be rejected — REQ-003
    /// requires a RELATIVE path, not merely a contained one.
    /// </summary>
    [Fact]
    public void Validate_ClientCertIsRootedButResolvesInsideSuiteDirectory_IsRejectedAsNotRelative()
    {
        var insideAbsolutePath = WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");
        var security = new SecuritySpec(
            "mtls", "9093", null, insideAbsolutePath, "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.mq.security.clientCert", result!.Message, StringComparison.Ordinal);
        Assert.Contains("must be a path relative to the suite directory", result.Message, StringComparison.Ordinal);
        // Must be rejected by the NEW rooted-path guard, never accidentally accepted.
        Assert.DoesNotContain("resolves outside", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A rooted path resolving OUTSIDE the suite directory must fail with the SAME
    /// "must be relative" message as the inside case above — not the pre-existing
    /// containment message, which (before this fix) could read as the absurd
    /// "resolves outside the suite directory" for a path whose only defect versus the
    /// suite directory was drive-letter casing (see the section header remarks).
    /// </summary>
    [Fact]
    public void Validate_ClientCertIsRootedAndResolvesOutsideSuiteDirectory_IsRejectedAsNotRelative()
    {
        var outsideAbsolutePath = WriteOutsideFile("client.pem");
        var security = new SecuritySpec(
            "mtls", "9093", null, outsideAbsolutePath, "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.mq.security.clientCert", result!.Message, StringComparison.Ordinal);
        Assert.Contains("must be a path relative to the suite directory", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("resolves outside", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 'serverArtifacts[].source' counterpart: a rooted source path is rejected the
    /// same way, naming the indexed field.
    /// </summary>
    [Fact]
    public void Validate_ServerArtifactSourceIsRooted_IsRejectedAsNotRelative()
    {
        var outsideAbsolutePath = WriteOutsideFile("kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new(outsideAbsolutePath, "/etc/kafka/secrets/kafka.server.keystore.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains(
            "environment.dependencies.events-kafka.security.serverArtifacts[0].source",
            result!.Message,
            StringComparison.Ordinal);
        Assert.Contains("must be a path relative to the suite directory", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ContainedPath_DoesNotTriggerContainmentError()
    {
        WriteSuiteFile("certs/ca.pem");
        var security = new SecuritySpec("tls", "6380", "./certs/ca.pem", null, null, null);
        var ast = BuildAst(dependencies: OneDependency("cache", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.Null(result);
    }

    // ── 5. serverArtifacts[].source: same containment + existence rules ─────

    [Fact]
    public void Validate_ServerArtifactSourceMissing_FailsNamingIndexedField()
    {
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/etc/kafka/secrets/kafka.server.keystore.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains(
            "environment.dependencies.events-kafka.security.serverArtifacts[0].source",
            result!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ServerArtifactSourceEscapesSuiteDirectory_FailsWithContainmentError()
    {
        var outsideFile = WriteOutsideFile("kafka.server.keystore.jks");
        var traversal = TraversalTo(outsideFile);
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new(traversal, "/etc/kafka/secrets/kafka.server.keystore.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("resolves outside the suite directory", result!.Message, StringComparison.Ordinal);
        Assert.Contains("serverArtifacts[0].source", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ServerArtifactSourceExistsInsideSuiteDirectory_ReturnsNull()
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/etc/kafka/secrets/kafka.server.keystore.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.Null(result);
    }

    /// <summary>
    /// <c>serverArtifacts[].target</c> is an IN-CONTAINER path — it must never be
    /// resolved or existence-checked against the host filesystem.
    /// </summary>
    [Fact]
    public void Validate_ServerArtifactTarget_IsNeverHostChecked()
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/this/path/does/not/exist/on/the/host.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.Null(result);
    }

    /// <summary>
    /// A <c>serverArtifacts[].target</c> whose shape a POSIX container path cannot mean is rejected
    /// at validation time — with the field named and REQ-018's preflight flag attached — rather
    /// than surfacing at topology-build time as an opaque daemon failure.
    /// </summary>
    /// <remarks>
    /// Deliberately mirrors <c>ServerArtifactInjectionTests</c>'s theory over the same inputs: the
    /// mapper repeats these rules, and the two stages reaching different answers about one declared
    /// path is the exact failure this file's own remarks warn about.
    /// </remarks>
    [Theory]
    [InlineData("/etc/kafka/../secrets/x.jks", "'.' or '..' segment")]
    [InlineData("/etc/kafka/..", "'.' or '..' segment")]
    [InlineData("/etc/kafka/./x.jks", "'.' or '..' segment")]
    [InlineData(@"/etc/kafka\secrets\ks.jks", "contains a backslash")]
    [InlineData("/etc//kafka/x.jks", "empty path segment")]
    public void Validate_ServerArtifactTargetWithAnUnrepresentablePosixShape_IsRejected(
        string target, string expected)
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", target),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains(expected, result!.Message, StringComparison.Ordinal);
        Assert.Contains("serverArtifacts[0].target", result.Message, StringComparison.Ordinal);
        Assert.True(result.IsSecurityPreflight);
    }

    /// <summary>
    /// The mirror that keeps the rule above from over-reaching: a dot INSIDE a segment is an
    /// ordinary file name, not a path segment.
    /// </summary>
    [Fact]
    public void Validate_ServerArtifactTargetWithADotInsideAFileName_IsAccepted()
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/etc/kafka/secrets/keystore..jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        Assert.Null(EnvironmentSecurityValidator.Validate(ast, _suiteDir));
    }

    // ── 6. Services get the same treatment as dependencies ───────────────────

    [Fact]
    public void Validate_ServiceSecurityBlock_IsAlsoChecked()
    {
        var security = new SecuritySpec("mtls", "8443", null, "./certs/client.pem", "./certs/client-key.pem", null);
        var ast = BuildAst(services: OneService("app", security));
        // Files not created.

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.services.app.security.clientCert", result!.Message, StringComparison.Ordinal);
    }

    // ── 7. Full happy path ────────────────────────────────────────────────────

    [Fact]
    public void Validate_FullyValidMtlsWithAllFilesPresent_ReturnsNull()
    {
        WriteSuiteFile("certs/ca.pem");
        WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");
        var security = new SecuritySpec(
            "mtls", "9093", "./certs/ca.pem", "./certs/client.pem", "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.Null(result);
    }

    // ── 8. Integration: wired into ProviderPipeline.Compile ─────────────────
    //
    // Proves this validator actually runs as part of the SAME pre-topology stage
    // `vouchfx validate` (ScenarioValidator's "Pipeline" stage) and pre-topology
    // `vouchfx run` (ScenarioRunner's run path) both already call, via
    // ProviderPipeline.Compile — not merely that the standalone method behaves
    // correctly in isolation.

    private static readonly StepKindRegistry s_pipelineRegistry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

    [Fact]
    public void Compile_DeclaredSecurityFileMissing_ReturnsPipelineFailure_AssembledIsNull()
    {
        const string yaml = """
            environment:
              dependencies:
                mq:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// filler"
            """;
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_pipelineRegistry);

        var result = ProviderPipeline.Compile(ast, s_pipelineRegistry, "TestSuite", _suiteDir);

        Assert.Null(result.Assembled);
        Assert.NotNull(result.Failure);
        Assert.Contains("clientCert", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ValidSecurityBlockWithFilesPresent_Succeeds()
    {
        WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");
        const string yaml = """
            environment:
              dependencies:
                mq:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: script.csharp
                code: "// filler"
            """;
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_pipelineRegistry);

        var result = ProviderPipeline.Compile(ast, s_pipelineRegistry, "TestSuite", _suiteDir);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
    }

    // ── 9. A DECLARED but blank value fails, distinct from an absent one (MAJOR fix) ──
    //
    // REQ-004(b) (section 2 above) says an UNDECLARED optional field is absent, not
    // missing. A declared-but-whitespace-only value is a THIRD case, different from
    // both: the schema's 'minLength: 1' rejects a literal "" outright on a real CLI
    // run, but counts characters, so "   " satisfies it and reaches this validator
    // undetected — with no provider Validate behind this authoring surface to catch it
    // a second time. Each case below asserts both failure AND that the message names
    // the exact field path, discriminating this new blank-path message from an
    // accidental "not found".

    [Fact]
    public void Validate_DeclaredCaCertIsWhitespaceOnly_FailsNamingCaCert()
    {
        var security = new SecuritySpec("tls", "6380", "   ", null, null, null);
        var ast = BuildAst(dependencies: OneDependency("cache", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.cache.security.caCert", result!.Message, StringComparison.Ordinal);
        Assert.Contains("blank", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The mtls 'clientCert' counterpart of the case above.</summary>
    [Fact]
    public void Validate_DeclaredClientCertIsWhitespaceOnly_FailsNamingClientCert()
    {
        var security = new SecuritySpec("mtls", "9093", null, "   ", "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.mq.security.clientCert", result!.Message, StringComparison.Ordinal);
        Assert.Contains("blank", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The 'serverArtifacts[].source' counterpart of the case above.</summary>
    [Fact]
    public void Validate_ServerArtifactSourceIsWhitespaceOnly_FailsNamingIndexedField()
    {
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("  ", "/etc/kafka/secrets/kafka.server.keystore.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains(
            "environment.dependencies.events-kafka.security.serverArtifacts[0].source",
            result!.Message,
            StringComparison.Ordinal);
        Assert.Contains("blank", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 10. IsContainedWithin: Ordinal comparison, not OrdinalIgnoreCase (MAJOR fix) ──

    /// <summary>
    /// A resolved path that differs from the suite directory ONLY in case (the shape a
    /// '..'-escape into a same-named-but-differently-cased sibling directory produces)
    /// must NOT be treated as contained. String-level and deterministic on any host
    /// filesystem — <see cref="EnvironmentSecurityValidator.IsContainedWithin"/> does a
    /// pure string prefix comparison, so this needs no real files and holds identically
    /// on a case-sensitive filesystem (Linux CI) or a case-insensitive one
    /// (Windows/macOS): an <see cref="StringComparison.OrdinalIgnoreCase"/> comparison
    /// would wrongly ACCEPT this pair as contained.
    /// </summary>
    [Fact]
    public void IsContainedWithin_ResolvedPathDiffersFromSuiteDirectoryOnlyInCase_IsNotContained()
    {
        var suiteDirectory = Path.Combine(Path.GetTempPath(), "vouchfx-case-pin", "Suite");
        var resolvedPath = Path.Combine(Path.GetTempPath(), "vouchfx-case-pin", "suite", "evil.pem");

        var result = EnvironmentSecurityValidator.IsContainedWithin(resolvedPath, suiteDirectory);

        Assert.False(result);
    }

    /// <summary>
    /// Positive control for the case above: identical casing throughout must still be
    /// recognised as contained under the Ordinal comparison.
    /// </summary>
    [Fact]
    public void IsContainedWithin_ResolvedPathUnderIdenticallyCasedSuiteDirectory_IsContained()
    {
        var suiteDirectory = Path.Combine(Path.GetTempPath(), "vouchfx-case-pin", "Suite");
        var resolvedPath = Path.Combine(suiteDirectory, "certs", "ca.pem");

        var result = EnvironmentSecurityValidator.IsContainedWithin(resolvedPath, suiteDirectory);

        Assert.True(result);
    }

    // ── 11. A malformed declared path fails cleanly, never as an unhandled exception ──

    /// <summary>
    /// A declared path <see cref="Path.GetFullPath(string)"/> cannot parse (an embedded
    /// NUL) must fail with a clean <see cref="ValidationFailure"/> naming the field —
    /// never let an exception escape through <see cref="ProviderPipeline.Compile"/> into
    /// <c>ScenarioValidator</c>'s unguarded Stage 3a (it has no try/catch of its own
    /// around that call).
    /// </summary>
    [Fact]
    public void Validate_DeclaredCaCertContainsEmbeddedNul_FailsCleanly_NeverThrows()
    {
        var security = new SecuritySpec("tls", "6380", "certs/ca\0evil.pem", null, null, null);
        var ast = BuildAst(dependencies: OneDependency("cache", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.cache.security.caCert", result!.Message, StringComparison.Ordinal);
    }

    // ── 12. serverArtifacts: a broken second entry names index 1, not 0 ──────

    [Fact]
    public void Validate_SecondServerArtifactMissing_NamesIndexOneNotZero()
    {
        WriteSuiteFile("certs/first.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/first.jks", "/etc/kafka/secrets/first.jks"),
            new("./certs/missing-second.jks", "/etc/kafka/secrets/second.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);
        var ast = BuildAst(dependencies: OneDependency("events-kafka", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains(
            "environment.dependencies.events-kafka.security.serverArtifacts[1].source",
            result!.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("serverArtifacts[0]", result.Message, StringComparison.Ordinal);
    }

    // ── 13. Ordering: services checked before dependencies ───────────────────

    /// <summary>
    /// When BOTH a service and a dependency declare a broken security block, the
    /// SERVICE failure is the one returned — services are checked before dependencies
    /// (see <see cref="EnvironmentSecurityValidator.Validate"/>'s own XML doc).
    /// </summary>
    [Fact]
    public void Validate_BothServiceAndDependencyBroken_ServiceFailureWinsOrdering()
    {
        var serviceSecurity = new SecuritySpec(
            "mtls", "8443", null, "./certs/svc-client.pem", "./certs/svc-client-key.pem", null);
        var dependencySecurity = new SecuritySpec(
            "mtls", "9093", null, "./certs/dep-client.pem", "./certs/dep-client-key.pem", null);
        // Neither the service's nor the dependency's certificate files are created —
        // both would independently fail if reached.
        var ast = BuildAst(
            services: OneService("app", serviceSecurity),
            dependencies: OneDependency("mq", dependencySecurity));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.services.app.security.clientCert", result!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("environment.dependencies", result.Message, StringComparison.Ordinal);
    }

    // ── 14. IsSecurityPreflight marker (critic MAJOR-3) ───────────────────────
    //
    // Gives the security preflight's failures a narrow, distinguishable signal that a
    // LATER slice (PR D, REQ-018) keys the unconditional non-zero exit on. Every failure
    // EnvironmentSecurityValidator itself raises must carry it; ProviderPipelineTests'
    // own ordinary-pipeline-failure test asserts the negative (an unrelated step
    // validation failure does NOT carry it) — see
    // Compile_StepValidationFails_ReturnsFailureNonNull_AssembledNull.

    /// <summary>
    /// Every failure <see cref="EnvironmentSecurityValidator.Validate"/> itself raises —
    /// here, a declared-but-blank <c>caCert</c> — carries
    /// <see cref="ValidationFailure.IsSecurityPreflight"/> set. G-MINOR-1 (gatekeeper,
    /// authenticated-infrastructure-mtls slice C): this is one of TWO producers of that
    /// marker in the pipeline now — <c>SecurityProfileWiringValidator</c> (REQ-022) also sets
    /// it — see <see cref="ValidationFailure.IsSecurityPreflight"/>'s own remarks for both and
    /// why; this file's own tests only ever drive the first.
    /// </summary>
    [Fact]
    public void Validate_Failure_CarriesSecurityPreflightMarker()
    {
        var security = new SecuritySpec("tls", "6380", "   ", null, null, null);
        var ast = BuildAst(dependencies: OneDependency("cache", security));

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.True(result!.IsSecurityPreflight,
            "A failure raised by EnvironmentSecurityValidator itself must carry IsSecurityPreflight.");
    }

    // ── 15. Copilot inline (thread 3698546487): unguarded suiteDirectory resolve ──

    /// <summary>
    /// <see cref="EnvironmentSecurityValidator.Validate"/>'s own
    /// <see cref="Path.GetFullPath(string)"/> call on <c>suiteDirectory</c> — the base
    /// every declared artefact resolves against — must fail cleanly with a
    /// <see cref="ValidationFailure"/> naming the suite directory as invalid, never let
    /// the exception escape uncaught (Copilot review finding, PR #345, thread
    /// 3698546487). Mirrors <see cref="Validate_DeclaredCaCertContainsEmbeddedNul_FailsCleanly_NeverThrows"/>'s
    /// own NUL-embedding technique, applied to the base directory itself this time —
    /// note this test calls <see cref="EnvironmentSecurityValidator.Validate"/> DIRECTLY
    /// with the malformed suiteDirectory (there is no AST/security-block prerequisite:
    /// the guard fires before any service/dependency is even inspected).
    /// </summary>
    [Fact]
    public void Validate_SuiteDirectoryContainsEmbeddedNul_FailsCleanly_NeverThrows()
    {
        var ast = BuildAst();
        var malformedSuiteDirectory = _suiteDir + "\0evil";

        var result = EnvironmentSecurityValidator.Validate(ast, malformedSuiteDirectory);

        Assert.NotNull(result);
        Assert.Contains(malformedSuiteDirectory, result!.Message, StringComparison.Ordinal);
        Assert.True(result.IsSecurityPreflight);
    }

    // ── 16. A service shape that can never be secured (spec gate SC-1) ────────

    /// <summary>
    /// <c>security</c> on a <c>project</c>-form service is rejected at VALIDATION time, not
    /// left to fail once containers are starting.
    /// </summary>
    /// <remarks>
    /// The JSON Schema accepts the combination — <c>$defs/service</c>'s project clause forbids
    /// several image-form-only fields but not <c>security</c>; grep that clause's <c>then</c>
    /// for the current roster rather than trusting a copy here — and
    /// <c>EnvironmentMapper.Map</c> then throws at topology-build time. That is the "validates
    /// but can never work" shape: loud, but only after a full topology cycle the author paid
    /// for to learn something knowable from the document alone. The mapper's own throw is
    /// deliberately kept as a fail-closed backstop for direct engine embedding, and
    /// <c>SecuredServiceEndpointTests.Map_SecurityOnAProjectFormService_...</c> still pins it.
    /// </remarks>
    [Fact]
    public void Validate_SecurityOnAProjectFormService_FailsAtValidationTime()
    {
        var services = new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
        {
            ["payments"] = new ServiceSpec(null, "./src/Payments/Payments.csproj", null, null, null)
            {
                Security = new SecuritySpec("tls", "8443", null, null, null, null),
            },
        };

        var result = EnvironmentSecurityValidator.Validate(BuildAst(services: services), _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.services.payments.security", result!.Message, StringComparison.Ordinal);
        Assert.Contains("'project'-form service cannot be secured", result.Message, StringComparison.Ordinal);
        Assert.True(result.IsSecurityPreflight);
    }

    /// <summary>
    /// The control: a <c>project</c>-form service with NO <c>security</c> block is untouched by
    /// the shape check, and an <c>image</c>-form service declaring <c>security</c> still passes.
    /// </summary>
    [Fact]
    public void Validate_ProjectFormServiceWithoutSecurity_IsUnaffected()
    {
        var services = new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
        {
            ["plain"] = new ServiceSpec(null, "./src/Payments/Payments.csproj", null, null, null),
            ["payments"] = new ServiceSpec("myorg/app:1.0", null, null, null, null)
            {
                Security = new SecuritySpec("tls", "8443", null, null, null, null),
            },
        };

        Assert.Null(EnvironmentSecurityValidator.Validate(BuildAst(services: services), _suiteDir));
    }

    // ── serverArtifacts[].target shape (REQ-016, slice E) ────────────────────
    //
    // Checked HERE, not only in EnvironmentMapper, because of the EXIT CODE. A fault raised from
    // the mapper surfaces as an ordinary environment-configuration error and exits 0 by default;
    // reaching it at this stage attaches IsSecurityPreflight, which REQ-018 keys the unconditional
    // non-zero exit on. It also tells an author at `vouchfx validate` time rather than after a
    // topology build. The schema's own `^/` pattern covers only the first of the three rules.

    /// <summary>
    /// A target naming a DIRECTORY has no file name for the engine to create. The schema's
    /// <c>^/</c> pattern accepts it, so this check is the only gate.
    /// </summary>
    [Fact]
    public void Validate_ServerArtifactTargetNamingADirectory_FailsAsSecurityPreflight()
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/etc/kafka/secrets/"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("events-kafka", security)), _suiteDir);

        Assert.NotNull(result);
        Assert.True(result!.IsSecurityPreflight);
        Assert.Contains("serverArtifacts[0].target", result.Message, StringComparison.Ordinal);
        Assert.Contains("names a directory, not a file", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two artefacts claiming ONE in-container path are rejected rather than silently resolved by
    /// declaration order — which one the broker's entrypoint ends up reading is not a decision
    /// this engine makes quietly.
    /// </summary>
    [Fact]
    public void Validate_TwoServerArtifactsWithTheSameTarget_FailsNamingTheSecondIndex()
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        WriteSuiteFile("certs/kafka.server.truststore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/etc/kafka/secrets/store.jks"),
            new("./certs/kafka.server.truststore.jks", "/etc/kafka/secrets/store.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("events-kafka", security)), _suiteDir);

        Assert.NotNull(result);
        Assert.True(result!.IsSecurityPreflight);
        Assert.Contains("serverArtifacts[1].target", result.Message, StringComparison.Ordinal);
        Assert.Contains("declared more than once", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relative target is rejected. Unreachable through the schema (whose <c>^/</c> pattern
    /// catches it first) but reachable through direct engine embedding, which is the whole reason
    /// this validator repeats the schema's rules rather than trusting them.
    /// </summary>
    [Fact]
    public void Validate_ServerArtifactTargetNotAbsolute_FailsAsSecurityPreflight()
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "etc/kafka/secrets/store.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("events-kafka", security)), _suiteDir);

        Assert.NotNull(result);
        Assert.True(result!.IsSecurityPreflight);
        Assert.Contains("must be an absolute path inside the container", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: two artefacts with DISTINCT absolute file targets — the ordinary
    /// keystore/truststore pair — pass cleanly.
    /// </summary>
    [Fact]
    public void Validate_TwoServerArtifactsWithDistinctTargets_ReturnsNull()
    {
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        WriteSuiteFile("certs/kafka.server.truststore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/etc/kafka/secrets/keystore.jks"),
            new("./certs/kafka.server.truststore.jks", "/etc/kafka/secrets/truststore.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);

        Assert.Null(EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("events-kafka", security)), _suiteDir));
    }

    // ── REQ-011 (#387): a `${secret:}` reference in a PATH-valued field is refused ──
    //
    // Measured behaviour before this rule (issue #387, CLI built from be12ebd):
    //
    //   environment.dependencies.broker.security.clientKey: file '${secret:env/CLIENT_KEY}'
    //     not found (resolved to '...\probe\${secret:env\CLIENT_KEY}').
    //
    // Two defects in one message: it blames the filesystem for a misuse of reference syntax,
    // and the '/' inside the token was eaten by path normalisation, so the echoed path is
    // split across a directory boundary that could never have existed. Both halves are pinned
    // below — the new text AND the ABSENCE of the old one. Asserting only the new text would
    // pass while the filesystem message still emitted alongside it.
    //
    // REQ-011's acceptance is explicit that this is "verified per field, measured separately,
    // not generalised from one", so there are four cases rather than one Theory: the clientKey
    // message is deliberately NOT the same as its three siblings', and a Theory over the four
    // would have to assert the weakest common text.

    private const string SecretReferenceValue = "${secret:env/CLIENT_KEY}";

    /// <summary>
    /// The shared half of REQ-011's assertion: the refusal names the field and the declared
    /// reference, says what the field takes instead and why a reference cannot work here, and
    /// echoes NO resolved filesystem path.
    /// </summary>
    private void AssertRefusedAsSecretReference(ValidationFailure? result, string fieldPath)
    {
        Assert.NotNull(result);
        Assert.True(result!.IsSecurityPreflight);
        Assert.Contains(fieldPath, result.Message, StringComparison.Ordinal);
        Assert.Contains(SecretReferenceValue, result.Message, StringComparison.Ordinal);
        // The opening claim must be one the CONTAINMENT guard actually established, and it must
        // NAME the sigil it found. "…is a secret reference" is asserted absent rather than merely
        // replaced: it was true of these four bare-token inputs and false of a path that merely
        // carries a token (pinned separately, on the mixed input, below), so a regression to it
        // would pass every assertion in this helper while being wrong about the wider input class
        // the guard catches.
        Assert.Contains(
            "uses secret-reference syntax ('${secret:')", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is a secret reference", result.Message, StringComparison.Ordinal);
        Assert.Contains("must exist inside the suite directory", result.Message, StringComparison.Ordinal);

        // The refusal must say WHY, and the reason is SCOPING, not timing. An earlier message said
        // a reference cannot resolve because "this material is loaded before any step runs, which
        // is when secret references resolve" — then recommended `clientKeyPassword`, which resolves
        // at that SAME pre-step moment and works, so the stated reason refuted its own remedy.
        // The true reason: these fields name a host FILE, and the secrets subsystem yields a VALUE.
        Assert.Contains("yields a VALUE", result.Message, StringComparison.Ordinal);
        Assert.Contains("is not a file and has no path", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("before any step runs", result.Message, StringComparison.Ordinal);

        // The filesystem message must be GONE, not merely joined by a better one.
        Assert.DoesNotContain("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolved to", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolves outside", result.Message, StringComparison.OrdinalIgnoreCase);

        // The garbled echo is half of #387: no resolved host path of any kind appears.
        Assert.DoesNotContain(_suiteDir, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_CaCertIsASecretReference_IsRefusedWithoutAFilesystemMessage()
    {
        var security = new SecuritySpec("tls", "6380", SecretReferenceValue, null, null, null);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("cache", security)), _suiteDir);

        AssertRefusedAsSecretReference(result, "environment.dependencies.cache.security.caCert");

        // Field-specific: the clientKeyPassword pointer belongs to clientKey ALONE.
        Assert.DoesNotContain("clientKeyPassword", result!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ClientCertIsASecretReference_IsRefusedWithoutAFilesystemMessage()
    {
        WriteSuiteFile("certs/client-key.pem");
        var security = new SecuritySpec(
            "mtls", "9093", null, SecretReferenceValue, "./certs/client-key.pem", null);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("mq", security)), _suiteDir);

        AssertRefusedAsSecretReference(result, "environment.dependencies.mq.security.clientCert");
        Assert.DoesNotContain("clientKeyPassword", result!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>clientKey</c> is the ONE field whose refusal also names <c>clientKeyPassword</c>: it is
    /// the supported way to keep the key encrypted at rest, which is what an author reaching for
    /// <c>${secret:}</c> here was almost certainly trying to achieve (#387, REQ-011).
    /// </summary>
    [Fact]
    public void Validate_ClientKeyIsASecretReference_IsRefused_AndNamesClientKeyPassword()
    {
        WriteSuiteFile("certs/client.pem");
        var security = new SecuritySpec(
            "mtls", "9093", null, "./certs/client.pem", SecretReferenceValue, null);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("mq", security)), _suiteDir);

        AssertRefusedAsSecretReference(result, "environment.dependencies.mq.security.clientKey");
        Assert.Contains("clientKeyPassword", result!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ServerArtifactSourceIsASecretReference_IsRefusedWithoutAFilesystemMessage()
    {
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new(SecretReferenceValue, "/etc/kafka/secrets/keystore.jks"),
        };
        var security = new SecuritySpec("tls", "9093", null, null, null, artifacts);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("events-kafka", security)), _suiteDir);

        AssertRefusedAsSecretReference(
            result, "environment.dependencies.events-kafka.security.serverArtifacts[0].source");
        Assert.DoesNotContain("clientKeyPassword", result!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sigil check must win over the ROOTED check that sits immediately below it, on an input
    /// where the ordering is OBSERVABLE. <c>/etc/${secret:env/CLIENT_KEY}</c> is both rooted and
    /// sigil-bearing, is reachable from ordinary YAML, and is refused as a secret reference —
    /// which is the actionable diagnosis; "must be a path relative to the suite directory" would
    /// send the author to fix the wrong thing.
    /// <para>
    /// An earlier form of this test used a BARE token, for which
    /// <see cref="Path.IsPathRooted(string)"/> is <see langword="false"/> — so the ordering was
    /// unobservable and a reordering mutant survived it. Both inputs are asserted here: the bare
    /// one for the property it does pin, the mixed one for the ordering.
    /// </para>
    /// </summary>
    [Fact]
    public void Validate_RootedPathContainingASecretReference_IsRefusedAsASecretReferenceNotAsRooted()
    {
        // The bare token is not rooted, which is why it alone cannot exercise the ordering.
        Assert.False(Path.IsPathRooted(SecretReferenceValue));

        const string rootedAndSigilBearing = "/etc/${secret:env/CLIENT_KEY}";
        Assert.True(Path.IsPathRooted(rootedAndSigilBearing));

        var security = new SecuritySpec("tls", "6380", rootedAndSigilBearing, null, null, null);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("cache", security)), _suiteDir);

        Assert.NotNull(result);
        Assert.Contains(
            "uses secret-reference syntax ('${secret:')", result!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "must be a path relative to the suite directory", result.Message, StringComparison.Ordinal);

        // THIS input is the one the retracted wording was FALSE about, so the accuracy of the
        // replacement is pinned here rather than only in the shared helper. '/etc/${secret:…}' is
        // a PATH that carries a reference; it is not itself one. The refusal must therefore make
        // a containment claim, must say the embedded shape is refused too (an author who reads
        // "is a secret reference" of this value will try trimming the path around the token), and
        // must not assert the identity the guard never tested.
        Assert.Contains("whole or embedded in a longer path", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is a secret reference", result.Message, StringComparison.Ordinal);
        Assert.Contains(rootedAndSigilBearing, result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-011's regression guard: the refusal must refuse ONLY the sigil. An ordinary relative
    /// path in every one of the four path-valued fields at once still validates clean.
    /// </summary>
    [Fact]
    public void Validate_OrdinaryRelativePathsInAllFourPathFields_StillValidateClean()
    {
        WriteSuiteFile("certs/ca.pem");
        WriteSuiteFile("certs/client.pem");
        WriteSuiteFile("certs/client-key.pem");
        WriteSuiteFile("certs/kafka.server.keystore.jks");
        var artifacts = new List<SecurityServerArtifactSpec>
        {
            new("./certs/kafka.server.keystore.jks", "/etc/kafka/secrets/keystore.jks"),
        };
        var security = new SecuritySpec(
            "mtls", "9093", "./certs/ca.pem", "./certs/client.pem", "./certs/client-key.pem", artifacts);

        Assert.Null(EnvironmentSecurityValidator.Validate(
            BuildAst(dependencies: OneDependency("mq", security)), _suiteDir));
    }

    /// <summary>
    /// A service's <c>security</c> block reaches the same chokepoint — the rule is not
    /// dependency-only (the field-path prefix is the only difference).
    /// </summary>
    [Fact]
    public void Validate_ServiceCaCertIsASecretReference_IsRefusedNamingTheServicePath()
    {
        var security = new SecuritySpec("tls", "8443", SecretReferenceValue, null, null, null);

        var result = EnvironmentSecurityValidator.Validate(
            BuildAst(services: OneService("api", security)), _suiteDir);

        AssertRefusedAsSecretReference(result, "environment.services.api.security.caCert");
    }
}
