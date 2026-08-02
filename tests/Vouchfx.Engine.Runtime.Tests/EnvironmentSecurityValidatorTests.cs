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
    /// A suite declaring <c>security: { mode: mtls, endpoint: 9093, clientCert: ...,
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

    // ── 3. REQ-004(a): a declared-but-missing file fails, naming the field + path ──

    [Fact]
    public void Validate_DeclaredClientCertMissing_FailsNamingFieldAndResolvedPath()
    {
        var security = new SecuritySpec("mtls", "9093", null, "./certs/client.pem", "./certs/client-key.pem", null);
        var ast = BuildAst(dependencies: OneDependency("mq", security));
        // Neither certificate file is created.

        var result = EnvironmentSecurityValidator.Validate(ast, _suiteDir);

        Assert.NotNull(result);
        Assert.Contains("environment.dependencies.mq.security.clientCert", result!.Message, StringComparison.Ordinal);
        var expectedResolved = Path.GetFullPath(Path.Combine(_suiteDir, "certs", "client.pem"));
        Assert.Contains(expectedResolved, result.Message, StringComparison.Ordinal);
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
                  type: rabbitmq
                  security:
                    mode: mtls
                    endpoint: 5671
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
                  type: rabbitmq
                  security:
                    mode: mtls
                    endpoint: 5671
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
}
