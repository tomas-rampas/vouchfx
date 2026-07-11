// StorageAssertS3Provider Docker-gated end-to-end execution tests.
//
// Mirrors DbAssertDynamodbDockerTests.cs / MetricsAssertPrometheusDockerTests.cs.  Proves that
// the emitted CSX fragment executes correctly against a real MinIO container started via
// SuiteTopology (the AppHost fixture with Aspire.AppHost.Sdk), using EnvironmentMapper's new
// "minio" dependency registration (Phase B).
//
// Scenarios exercised:
//   1. Pass    — object exists; size/contentType/metadata all match.
//   2. Fail    — size mismatch.
//   3. Pass    — sha256 digest matches (triggers the bounded GET body path).
//   4. Fail    — contentContains substring not found in the body (bounded GET path).
//   5. Fail    — key does not exist (expect.exists defaults to true).
//   6. Pass    — key does not exist AND expect.exists:false.
//   7. EnvironmentError — connection key absent from Vars.
//   8. RETRY convergence — the step starts against an ABSENT object (first attempts Fail),
//      a concurrent PUT lands the object shortly after, and the engine-owned RETRY wrapper
//      (StepCompilePlan Retry:true) converges to Pass — proving verifyMode: RETRY's
//      eventually-consumed idiom round-trips against a live container end to end.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~StorageAssertS3Docker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.StorageAssert.S3;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for <see cref="StorageAssertS3Provider"/>.
/// Requires a running Docker daemon with the <c>minio/minio</c> image available.
/// </summary>
public sealed class StorageAssertS3DockerTests
{
    private readonly ITestOutputHelper _output;

    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private const string DepName = "testminio";
    private const string BucketName = "reports";

    public StorageAssertS3DockerTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(Amazon.S3.AmazonS3Client).Assembly.Location,
        typeof(Amazon.Runtime.BasicAWSCredentials).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Security.Cryptography.SHA256).Assembly.Location,
    };

    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId, IReadOnlyDictionary<string, CaptureExpr>? captures = null)
        {
            StepId = stepId;
            CaptureExprs = captures ?? new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
            Captures = CaptureExprs.ToDictionary(kv => kv.Key, kv => kv.Value.Expression, StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; }
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "minio", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static (string ServiceUrl, string AccessKey, string SecretKey) ParseConnectionString(string connStr)
    {
        string serviceUrl = string.Empty, accessKey = string.Empty, secretKey = string.Empty;
        foreach (var part in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            if (string.Equals(k, "ServiceURL", StringComparison.OrdinalIgnoreCase)) serviceUrl = v;
            else if (string.Equals(k, "AccessKey", StringComparison.OrdinalIgnoreCase)) accessKey = v;
            else if (string.Equals(k, "SecretKey", StringComparison.OrdinalIgnoreCase)) secretKey = v;
        }
        return (serviceUrl, accessKey, secretKey);
    }

    private static AmazonS3Client BuildClient(string connStr)
    {
        var (serviceUrl, accessKey, secretKey) = ParseConnectionString(connStr);
        return new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true });
    }

    private static async Task EnsureBucketAsync(AmazonS3Client client)
    {
        try
        {
            await client.PutBucketAsync(BucketName).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (
            string.Equals(ex.ErrorCode, "BucketAlreadyOwnedByYou", StringComparison.Ordinal)
            || string.Equals(ex.ErrorCode, "BucketAlreadyExists", StringComparison.Ordinal))
        {
            // Already created by a prior run against the same container instance.
        }
    }

    private static async Task PutObjectAsync(
        AmazonS3Client client, string key, string body, string contentType = "text/plain",
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var request = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = key,
            ContentBody = body,
            ContentType = contentType,
        };
        if (metadata is not null)
        {
            foreach (var (k, v) in metadata)
                request.Metadata.Add(k, v);
        }
        await client.PutObjectAsync(request).ConfigureAwait(false);
    }

    private static async Task<StepOutcome> RunStepAsync(
        StorageAssertS3Model model,
        string stepId,
        Dictionary<string, object?> vars,
        IReadOnlyDictionary<string, CaptureExpr>? captures = null)
    {
        var provider = new StorageAssertS3Provider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId, captures));

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

    private static async Task<StepOutcome> RunStepWithRetryAsync(
        StorageAssertS3Model model,
        string stepId,
        Dictionary<string, object?> vars,
        long timeoutMs)
    {
        var provider = new StorageAssertS3Provider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var plan = new StepCompilePlan(stepId, fragment, Retry: true, TimeoutMs: timeoutMs, PollIntervalMs: 300L);
        var assembled = CsxAssembler.Assemble(new[] { plan });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey));
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── Test cases ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_MatchingExistsSizeContentTypeAndMetadata_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        const string body = "order-export-contents";
        var client = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(client);
            await PutObjectAsync(client, "exports/report.csv", body, "text/csv",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "orders-api" });
        }
        finally
        {
            client.Dispose();
        }

        var model = new StorageAssertS3Model(
            DepName, BucketName, "exports/report.csv",
            new StorageExpectation(
                Exists: true,
                Size: body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MinSize: null, Sha256: null, ContentContains: null,
                ContentType: "text/csv",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "orders-api" }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["etag"] = new CaptureExpr(CaptureFormat.JsonPath, "$.etag"),
        };

        var outcome = await RunStepAsync(model, "assert-s3-pass", vars, captures);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.True(vars.TryGetValue("etag", out var etag) && !string.IsNullOrEmpty(etag as string),
            "etag must be captured on Pass.");
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_SizeMismatch_ReturnsFail()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        var client = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(client);
            await PutObjectAsync(client, "exports/size-check.csv", "twelve-bytes!");
        }
        finally
        {
            client.Dispose();
        }

        var model = new StorageAssertS3Model(
            DepName, BucketName, "exports/size-check.csv",
            new StorageExpectation(true, "999", null, null, null, null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-s3-size-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"field\":\"size\"", outcome.Observation, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_Sha256Match_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        const string body = "sha256-fixture-body";
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var client = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(client);
            await PutObjectAsync(client, "exports/hash-check.csv", body);
        }
        finally
        {
            client.Dispose();
        }

        var model = new StorageAssertS3Model(
            DepName, BucketName, "exports/hash-check.csv",
            new StorageExpectation(true, null, null, expectedHash, null, null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-s3-sha256-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_ContentContainsMismatch_ReturnsFail()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        var client = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(client);
            await PutObjectAsync(client, "exports/contains-check.csv", "the quick brown fox");
        }
        finally
        {
            client.Dispose();
        }

        var model = new StorageAssertS3Model(
            DepName, BucketName, "exports/contains-check.csv",
            new StorageExpectation(true, null, null, null, "lazy dog", null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-s3-contains-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"field\":\"contentContains\"", outcome.Observation, StringComparison.Ordinal);
        // §17 spirit: never echo the searched substring or the body.
        Assert.DoesNotContain("lazy dog", outcome.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain("quick brown fox", outcome.Observation, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentObject_ExistsDefaultTrue_ReturnsFail()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        var client = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(client);
        }
        finally
        {
            client.Dispose();
        }

        var model = new StorageAssertS3Model(
            DepName, BucketName, "exports/does-not-exist.csv",
            new StorageExpectation(null, null, null, null, null, null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-s3-absent-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"exists\":false", outcome.Observation, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentObject_ExistsFalse_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        var client = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(client);
        }
        finally
        {
            client.Dispose();
        }

        var model = new StorageAssertS3Model(
            DepName, BucketName, "exports/still-does-not-exist.csv",
            new StorageExpectation(false, null, null, null, null, null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-s3-existsfalse-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = new StorageAssertS3Model(
            "missing-dep", BucketName, "exports/report.csv",
            new StorageExpectation(true, null, null, null, null, null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "assert-s3-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.Contains("error", outcome.Observation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// RETRY convergence (the family's canonical "eventually-consumed" idiom, §5.9): the
    /// object is ABSENT when the RETRY-wrapped step starts (the first poll attempts must
    /// Fail), a concurrent task PUTs it shortly after, and the overall step outcome must
    /// still converge to Pass before the timeout — proving the engine-owned RetryRunner is
    /// genuinely re-polling a live container, not just compiling the wrapper shape.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_RetryConvergence_PutAfterFirstAttempt_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        const string key = "exports/eventually-written.csv";
        const string body = "written-after-first-poll";

        var seedClient = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(seedClient);
            // Defensive: ensure the object is ABSENT at the start of this test (a prior failed
            // run against the same container could have left it behind).
            try
            {
                await seedClient.DeleteObjectAsync(BucketName, key);
            }
            catch (AmazonS3Exception) { /* absent already — fine */ }
        }
        finally
        {
            seedClient.Dispose();
        }

        // Fire the delayed PUT concurrently with the RETRY-wrapped step below.
        var putTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            var client = BuildClient(connStr!);
            try
            {
                await PutObjectAsync(client, key, body);
            }
            finally
            {
                client.Dispose();
            }
        });

        var model = new StorageAssertS3Model(
            DepName, BucketName, key,
            new StorageExpectation(true, null, null, null, null, null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepWithRetryAsync(model, "assert-s3-retry-convergence", vars, timeoutMs: 10000);
        await putTask;

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }
}
