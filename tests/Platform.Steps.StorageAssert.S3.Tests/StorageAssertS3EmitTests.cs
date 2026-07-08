// Tests for StorageAssertS3Provider — CSX emitter + full compile-and-run round-trips
// (non-docker; no S3-compatible endpoint is reachable in these tests, so only the
// EnvironmentError / secret-reference paths are exercised here — the full HEAD/GET
// exists/size/sha256/contentContains/capture behaviour is covered by
// StorageAssertS3DockerTests against a real MinIO container).
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' anywhere in the fragment.
//   3.  Emit: helper class is named 'StorageAssertS3_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources.
//   6.  Full compile-and-run: EnvironmentError when the conn key is absent from Vars.
//   7.  Full compile-and-run: EnvironmentError when the connection is refused (unreachable
//       ServiceURL) — proves the emitted CSX actually compiles and executes end-to-end.
//   8.  Full compile-and-run: EnvironmentError via a missing ${secret:env/…} in bucket,
//       REFERENCE-ONLY observation (§17), no network call ever attempted.
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.StorageAssert.S3;
using Xunit;

namespace Platform.Steps.StorageAssert.S3.Tests;

public sealed class StorageAssertS3EmitTests
{
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private readonly StorageAssertS3Provider _provider = new();

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(Amazon.S3.AmazonS3Client).Assembly.Location,
        typeof(Amazon.Runtime.BasicAWSCredentials).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        // The REAL engine (ScenarioRunner) always passes the full Trusted-Platform-Assemblies
        // list, which includes these two BCL facade assemblies automatically; this curated
        // test-only reference list must supply them explicitly since it does not use the TPA.
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Security.Cryptography.SHA256).Assembly.Location,
    };

    private static StorageAssertS3Model GetModel(
        string target = "uploads",
        string bucket = "reports",
        string key = "exports/report.csv",
        bool? exists = null,
        string? size = null) =>
        new StorageAssertS3Model(target, bucket, key,
            new StorageExpectation(exists, size, null, null, null, null, null));

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("s3-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'));
        Assert.True(block.EndsWith('}'));
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("my-step"));
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsStorageAssertS3PrefixedClass()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("s3-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("StorageAssertS3_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "s3-step-one";
        var safeId = CsxFragment.SanitiseId(rawId);
        var fragment = _provider.Emit(GetModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. RequiredHelpers includes Substitute_Helpers and Secret_Helpers ────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("h-check"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 6. Full compile-and-run: EnvironmentError when conn key absent ───────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var outcome = await RunStepAsync(GetModel(), "s3-absent-conn", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0);
        Assert.NotNull(outcome.Observation);
    }

    // ── 7. Full compile-and-run: connection refused ───────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_ConnectionRefused_ReturnsEnvironmentError()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("uploads")] = "ServiceURL=http://127.0.0.1:1;AccessKey=vouchfx-minio;SecretKey=vouchfx-minio-secret",
        };

        var outcome = await RunStepAsync(GetModel(exists: true), "s3-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 8. Missing secret in bucket → EnvironmentError, reference-only ───────

    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInBucket_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_S3_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "s3-secret-step";
            var model = new StorageAssertS3Model(
                "uploads", $"${{secret:env/{envName}}}", "exports/report.csv",
                new StorageExpectation(true, null, null, null, null, null, null));

            var fragment = _provider.Emit(model, new StubCompileContext(stepId));
            var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection("uploads")] = "ServiceURL=http://127.0.0.1:1;AccessKey=vouchfx-minio;SecretKey=vouchfx-minio-secret",
            };

            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
            Assert.True(vars.ContainsKey(outcomeKey));

            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<StepOutcome> RunStepAsync(
        StorageAssertS3Model model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }
}
