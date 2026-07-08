// DbAssertDynamodbProvider Docker-gated end-to-end execution tests.
//
// Mirrors DbAssertMongodbDockerTests.cs.  Proves that the emitted CSX fragment executes
// correctly against a real DynamoDB Local container started via SuiteTopology (the AppHost
// fixture with Aspire.AppHost.Sdk), using EnvironmentMapper's new "dynamodb" dependency
// registration (Phase B).
//
// Scenarios exercised:
//   1. Pass          — key matches the seeded item and the field expectation is met.
//   2. Fail           — key matches the seeded item but a field expectation is wrong.
//   3. Fail           — key does not match any item (absent, expect.exists defaults to true).
//   4. EnvironmentError — connection key absent from Vars.
//   5. The SAME model compiled through the engine-owned RETRY wrapper (StepCompilePlan
//      Retry:true) still yields Pass — proving verifyMode: RETRY round-trips end to end.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~DbAssertDynamodbDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Compilation;
using Platform.Engine.Orchestration;
using Platform.Sdk;
using Platform.Steps.DbAssert.Dynamodb;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for <see cref="DbAssertDynamodbProvider"/>.
/// Requires a running Docker daemon with the <c>amazon/dynamodb-local</c> image available.
/// </summary>
public sealed class DbAssertDynamodbDockerTests
{
    private readonly ITestOutputHelper _output;

    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private const string DepName = "testdynamo";
    private const string TableName = "Orders";

    public DbAssertDynamodbDockerTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(Amazon.DynamoDBv2.AmazonDynamoDBClient).Assembly.Location,
        typeof(Amazon.Runtime.BasicAWSCredentials).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
    };

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

    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "dynamodb", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Parses the "ServiceURL=…;AccessKey=…;SecretKey=…" form EnvironmentMapper's "dynamodb"
    /// registration produces (test-local copy of the provider's own parse logic).
    /// </summary>
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

    /// <summary>
    /// Creates the Orders table (on-demand billing, partition key "orderId") if it does not
    /// already exist, waits for it to become ACTIVE, then puts one seed item.
    /// </summary>
    private static async Task SeedAsync(string connStr)
    {
        var (serviceUrl, accessKey, secretKey) = ParseConnectionString(connStr);
        AmazonDynamoDBClient? client = null;
        try
        {
            client = new AmazonDynamoDBClient(
                new BasicAWSCredentials(accessKey, secretKey),
                new AmazonDynamoDBConfig { ServiceURL = serviceUrl });

            try
            {
                await client.CreateTableAsync(new CreateTableRequest
                {
                    TableName = TableName,
                    AttributeDefinitions = new List<AttributeDefinition>
                    {
                        new AttributeDefinition("orderId", ScalarAttributeType.S),
                    },
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement("orderId", KeyType.HASH),
                    },
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                }).ConfigureAwait(false);
            }
            catch (ResourceInUseException)
            {
                // Table already exists from a prior run against the same container instance.
            }

            // dynamodb-local creates tables near-instantly, but poll briefly for ACTIVE to be
            // defensive against slow CI hardware.
            for (var i = 0; i < 20; i++)
            {
                var describe = await client.DescribeTableAsync(TableName).ConfigureAwait(false);
                if (describe.Table.TableStatus == TableStatus.ACTIVE)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }

            await client.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    ["orderId"] = new AttributeValue { S = "1" },
                    ["status"] = new AttributeValue { S = "SHIPPED" },
                    ["quantity"] = new AttributeValue { N = "3" },
                },
            }).ConfigureAwait(false);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static async Task<StepOutcome> RunStepAsync(
        DbAssertDynamodbModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new DbAssertDynamodbProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

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

    /// <summary>
    /// Compiles the same model through the engine-owned RETRY wrapper
    /// (<c>StepCompilePlan Retry:true</c>) — proves <c>verifyMode: RETRY</c> round-trips
    /// against a live container.
    /// </summary>
    private static async Task<StepOutcome> RunStepWithRetryAsync(
        DbAssertDynamodbModel model,
        string stepId,
        Dictionary<string, object?> vars,
        long timeoutMs = 5000)
    {
        var provider = new DbAssertDynamodbProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var plan = new StepCompilePlan(stepId, fragment, Retry: true, TimeoutMs: timeoutMs, PollIntervalMs: null);
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
    public async Task Execute_MatchingItemAndFields_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty connection string.");

        await SeedAsync(connStr!);
        _output.WriteLine("Seed: Orders table populated with {orderId:1, status:SHIPPED, quantity:3}.");

        var model = new DbAssertDynamodbModel(
            Target: DepName,
            Table: TableName,
            Key: "{\"orderId\":\"1\"}",
            Expect: new DynamoExpectation(
                Exists: true,
                Item: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = "SHIPPED",
                    ["quantity"] = "3",
                }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-dynamo-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AttributeMismatch_ReturnsFail()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new DbAssertDynamodbModel(
            Target: DepName,
            Table: TableName,
            Key: "{\"orderId\":\"1\"}",
            Expect: new DynamoExpectation(
                Exists: true,
                Item: new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "PENDING" }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-dynamo-mismatch", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("status", outcome.Observation, StringComparison.Ordinal);
        Assert.Contains("PENDING", outcome.Observation, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentItem_ReturnsFail()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new DbAssertDynamodbModel(
            Target: DepName,
            Table: TableName,
            Key: "{\"orderId\":\"does-not-exist\"}",
            Expect: new DynamoExpectation(Exists: true, Item: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-dynamo-absent", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"exists\":false", outcome.Observation, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentConnKey_ReturnsEnvironmentError()
    {
        // No topology needed — the EnvironmentError is detected before any network call.
        var model = new DbAssertDynamodbModel(
            Target: "missing-dep",
            Table: TableName,
            Key: "{\"orderId\":\"1\"}",
            Expect: new DynamoExpectation(Exists: true, Item: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "assert-dynamo-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.Contains("error", outcome.Observation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_RetryWrapped_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new DbAssertDynamodbModel(
            Target: DepName,
            Table: TableName,
            Key: "{\"orderId\":\"1\"}",
            Expect: new DynamoExpectation(
                Exists: true,
                Item: new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "SHIPPED" }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepWithRetryAsync(model, "assert-dynamo-retry", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }
}
