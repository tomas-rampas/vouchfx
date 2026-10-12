// Execute real emitted S3 scripts against a deterministic HTTP peer. The peer
// records operation/status only; author values never enter test diagnostics.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Abstractions.Webhooks;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Steps.StorageAssert.S3.Tests;

public sealed class StorageAssertS3ProtocolTests
{
    private const string StepId = "s3-protocol";
    private const string WarmupStepId = "s3-protocol-setup";
    private const int BodyCap = 16 * 1024 * 1024;
    private static readonly string[] s_headObject = { "HEAD object" };
    private static readonly string[] s_headThenBucket = { "HEAD object", "HEAD bucket" };
    private static readonly string[] s_headThenGet = { "HEAD object", "GET object" };
    private static readonly string[] s_headGetMethods = { "HEAD", "GET" };
    private readonly ITestOutputHelper _output;

    public StorageAssertS3ProtocolTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(200, 403, true, Verdict.Pass, 0)]
    [InlineData(200, 403, false, Verdict.Fail, 0)]
    [InlineData(404, 200, true, Verdict.Fail, 1)]
    [InlineData(404, 200, false, Verdict.Pass, 1)]
    [InlineData(404, 404, true, Verdict.EnvironmentError, 1)]
    [InlineData(404, 404, false, Verdict.EnvironmentError, 1)]
    [InlineData(404, 403, true, Verdict.EnvironmentError, 1)]
    [InlineData(404, 403, false, Verdict.EnvironmentError, 1)]
    [InlineData(404, 500, true, Verdict.EnvironmentError, 1)]
    [InlineData(404, 500, false, Verdict.EnvironmentError, 1)]
    public async Task Object404_RequiresSuccessfulBucketConfirmation(
        int objectStatus, int bucketStatus, bool exists, Verdict expected, int bucketRequests)
    {
        await using var peer = new S3Peer(request => Task.FromResult(
            new Reply(request.Kind == "object" ? objectStatus : bucketStatus)));
        var result = await RunAsync(peer, new StorageExpectation(exists, null, null, null, null, null, null));
        Trace(peer, result);

        Assert.Equal(expected, result.Outcome.Verdict);
        Assert.Equal(bucketRequests, peer.Requests.Count(x => x.Kind == "bucket"));
        Assert.Equal(s_headObject.Concat(Enumerable.Repeat("HEAD bucket", bucketRequests)),
            peer.Requests.Select(x => x.Method + " " + x.Kind));
        Assert.All(peer.Requests, x => Assert.Equal("HEAD", x.Method));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BucketConfirmation_TruncatedResponse_IsEnvironmentError(bool exists)
    {
        await using var peer = new S3Peer(request => Task.FromResult(request.Kind == "object"
            ? new Reply(404) : new Reply(0, DropConnection: true)));
        var result = await RunAsync(peer, new StorageExpectation(exists, null, null, null, null, null, null));
        Trace(peer, result);

        Assert.Equal(Verdict.EnvironmentError, result.Outcome.Verdict);
        Assert.Equal(s_headThenBucket, peer.Requests.Select(x => x.Method + " " + x.Kind));
        Assert.Equal(0, peer.Requests[1].Status);
        using var observation = JsonDocument.Parse(result.Outcome.Observation!);
        var error = Assert.Single(observation.RootElement.EnumerateObject());
        Assert.Equal("error", error.Name);
        Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", error.Value.GetString()!);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("minSize")]
    [InlineData("contentType")]
    [InlineData("metadata")]
    [InlineData("sha256")]
    public async Task Mismatch_DoesNotDiscloseResolvedExpectedOrReturnedContent(string field)
    {
        const string expectedCanary = "author-secret-canary";
        const string actualCanary = "returned-content-canary";
        const string numberCanary = "987654321";
        var expectedValue = field is "size" or "minSize" ? numberCanary : expectedCanary;
        using var resolver = new CanaryResolver(expectedValue);
        var expectation = field switch
        {
            "size" => new StorageExpectation(true, "${secret:test/value}", null, null, null, null, null),
            "minSize" => new StorageExpectation(true, null, "${secret:test/value}", null, null, null, null),
            "contentType" => new StorageExpectation(true, null, null, null, null, "${secret:test/value}", null),
            "metadata" => new StorageExpectation(true, null, null, null, null, null,
                new Dictionary<string, string> { ["source"] = "${secret:test/value}" }),
            _ => new StorageExpectation(true, null, null, "${secret:test/value}", null, null, null),
        };
        await using var peer = new S3Peer(_ => Task.FromResult(new Reply(200,
            Body: Encoding.UTF8.GetBytes("body-secret-canary"),
            ContentType: actualCanary, Metadata: actualCanary)));
        var result = await RunAsync(peer, expectation, resolver: resolver);
        Trace(peer, result);

        Assert.Equal(Verdict.Fail, result.Outcome.Verdict);
        Assert.True(resolver.Calls > 0);
        var json = JsonDocument.Parse(result.Outcome.Observation!);
        Assert.False(json.RootElement.TryGetProperty("expected", out _));
        Assert.False(json.RootElement.TryGetProperty("actual", out _) && field != "sha256");
        Assert.False(json.RootElement.GetProperty("matched").GetBoolean());
        Assert.DoesNotContain(expectedValue, result.Outcome.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain(actualCanary, result.Outcome.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain("body-secret-canary", result.Outcome.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedValue, JsonSerializer.Serialize(result.Live), StringComparison.Ordinal);
        Assert.DoesNotContain(actualCanary, JsonSerializer.Serialize(result.Live), StringComparison.Ordinal);
        Assert.Equal(field == "sha256" ? 1 : 0, peer.Requests.Count(x => x.Method == "GET"));
    }

    [Theory]
    [InlineData(BodyCap, BodyCap, Verdict.Pass, 1)]
    [InlineData(BodyCap + 1, BodyCap + 1, Verdict.Fail, 0)]
    [InlineData(BodyCap, BodyCap + 1, Verdict.Fail, 1)]
    public async Task BodyRead_EnforcesExactCapAndStaleHeadBoundary(
        int headLength, int bodyLength, Verdict expected, int getRequests)
    {
        var body = Enumerable.Repeat((byte)'x', bodyLength).ToArray();
        await using var peer = new S3Peer(_ => Task.FromResult(new Reply(200,
            Body: body, HeadLength: headLength)));
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var result = await RunAsync(peer, new StorageExpectation(true, null, null, hash, "xxx", null, null));
        Trace(peer, result);

        Assert.Equal(expected, result.Outcome.Verdict);
        Assert.Equal(getRequests, peer.Requests.Count(x => x.Method == "GET"));
        if (expected == Verdict.Fail)
            Assert.Equal("bodyCap", JsonDocument.Parse(result.Outcome.Observation!).RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public async Task ContentContains_MismatchReportsOnlyMatchAndLength()
    {
        await using var peer = new S3Peer(_ => Task.FromResult(new Reply(200,
            Body: Encoding.UTF8.GetBytes("actual-body-canary"))));
        var result = await RunAsync(peer, new StorageExpectation(true, null, null, null,
            "missing-substring-canary", null, null));
        Trace(peer, result);
        Assert.Equal(Verdict.Fail, result.Outcome.Verdict);
        Assert.DoesNotContain("canary", result.Outcome.Observation!, StringComparison.Ordinal);
        Assert.Equal(18, JsonDocument.Parse(result.Outcome.Observation!).RootElement.GetProperty("bodyLength").GetInt32());
        Assert.Equal(s_headGetMethods, peer.Requests.Select(x => x.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateTimeout_DuringBucketConfirmationOrBodyRead_IsInconclusive(bool bodyRead)
    {
        var armed = 0;
        var setupCompleted = new TaskCompletionSource<StepOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new S3Peer(request =>
        {
            var hold = Volatile.Read(ref armed) == 1 && (bodyRead ? request.Method == "GET" : request.Kind == "bucket");
            return Task.FromResult(new Reply(bodyRead ? 200 : request.Kind == "object" ? 404 : 200,
                Body: Enumerable.Repeat((byte)'x', 100).ToArray(), HoldHeaders: hold && !bodyRead, HoldBody: hold && bodyRead));
        });
        var expectation = new StorageExpectation(true, null, null, null, bodyRead ? "x" : null, null, null);
        // Exercise the same SDK/helper and real HTTP path before starting the short step budget.
        // The setup completion signal arms the blocked response; setup time is not timeout evidence.
        var invocation = PrepareRun(peer, expectation, timeoutMs: 1500,
            warmupExpectation: bodyRead ? expectation : new StorageExpectation(false, null, null, null, null, null, null),
            onWarmupCompleted: outcome =>
            {
                setupCompleted.TrySetResult(outcome);
                if (outcome?.Verdict == Verdict.Pass)
                    Volatile.Write(ref armed, 1);
            });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = RoslynScriptCompiler.RunIsolatedAsync(invocation.Compiled, invocation.Globals, cancellationToken: safety.Token);
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            var outcome = Assert.IsType<StepOutcome>(invocation.Vars[VarKeys.Outcome(CsxFragment.SanitiseId(StepId))]);
            var result = new RunResult(outcome, new List<AttemptRecord>(), invocation.Live);
            Trace(peer, result);

            var setup = Assert.IsType<StepOutcome>(await setupCompleted.Task.WaitAsync(safety.Token));
            Assert.Equal(Verdict.Pass, setup.Verdict);
            Assert.Equal(2, invocation.Live.CompletionCount);
            Assert.Same(setup, invocation.Live.Completions[WarmupStepId]);
            Assert.Same(outcome, invocation.Live.Completions[StepId]);
            Assert.True(peer.HeldOperation.IsCompletedSuccessfully, "The provider must reach the intended blocked request.");
            var held = await peer.HeldOperation;
            Assert.Equal(bodyRead ? "GET object" : "HEAD bucket", held);
            Assert.Equal(held, await peer.ClientDisconnected.WaitAsync(safety.Token));
            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
            var observation = JsonDocument.Parse(outcome.Observation!);
            Assert.Equal("step-timeout", observation.RootElement.GetProperty("reason").GetString());
            Assert.Equal(1500, observation.RootElement.GetProperty("timeoutMs").GetInt64());
            var expectedRequests = bodyRead ? s_headThenGet : s_headThenBucket;
            Assert.Equal(expectedRequests.Concat(expectedRequests), peer.Requests.Select(x => x.Method + " " + x.Kind));
        }
        finally
        {
            safety.Cancel();
            await peer.DisposeAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (safety.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task CallerCancellation_DuringBucketConfirmationOrBodyRead_CancelsOperationWithoutVerdict(
        bool bodyRead, bool loadedScript, bool retry)
    {
        using var resolver = new CanaryResolver("caller-secret-canary");
        await using var peer = new S3Peer(request =>
        {
            var hold = bodyRead ? request.Method == "GET" : request.Kind == "bucket";
            return Task.FromResult(new Reply(bodyRead ? 200 : request.Kind == "object" ? 404 : 200,
                Body: Encoding.UTF8.GetBytes("returned-body-canary"),
                HoldHeaders: hold && !bodyRead, HoldBody: hold && bodyRead));
        });
        var invocation = PrepareRun(peer, new StorageExpectation(true, null, null, null,
            bodyRead ? "${secret:test/value}" : null, null, null), timeoutMs: 60000, retry: retry,
            resolver: resolver, tail: "Vars[\"caller-cancellation-tail\"] = true;");
        using var caller = new CancellationTokenSource();
        using var loaded = loadedScript ? RoslynScriptCompiler.Load(invocation.Compiled) : null;
        var run = loadedScript
            ? loaded!.InvokeAsync(invocation.Globals, caller.Token)
            : RoslynScriptCompiler.RunIsolatedAsync(invocation.Compiled, invocation.Globals, cancellationToken: caller.Token);
        try
        {
            var held = await peer.HeldOperation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(bodyRead ? "GET object" : "HEAD bucket", held);
            Assert.False(run.IsCompleted, "The operation must remain blocked before caller cancellation.");
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(run.IsCanceled, "Cancellation must complete the actual script task.");
            Assert.Equal(held, await peer.ClientDisconnected.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(bodyRead ? s_headThenGet : s_headThenBucket,
                peer.Requests.Select(x => x.Method + " " + x.Kind));
            Assert.False(invocation.Vars.ContainsKey(VarKeys.Outcome(CsxFragment.SanitiseId(StepId))));
            Assert.False(invocation.Vars.ContainsKey("caller-cancellation-tail"));
            Assert.Equal(0, invocation.Live.CompletionCount);
            Assert.Null(invocation.Live.Completed);
            Assert.Empty(invocation.Live.Attempts);
            Assert.DoesNotContain("canary", JsonSerializer.Serialize(invocation.Live), StringComparison.Ordinal);
            Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
            _output.WriteLine(JsonSerializer.Serialize(new
            {
                held,
                operationDisconnected = true,
                scriptTaskCanceled = run.IsCanceled,
                finalOutcome = false,
                completionEvents = invocation.Live.CompletionCount,
                loadedScript,
                retry,
                stepBudgetMs = 60000,
            }));
        }
        finally
        {
            // Finish any in-flight invocation before disposing a reusable loaded script.
            caller.Cancel();
            await peer.DisposeAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Retry_ConfirmedMissingObjectThenPut_PassesAndPreservesAttempts()
    {
        var present = 0;
        var confirmedMissing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new S3Peer(request =>
        {
            if (request.Method == "PUT")
            {
                Interlocked.Exchange(ref present, 1);
                return Task.FromResult(new Reply(200));
            }
            // The controller PUT waits for the first recorded absent attempt.
            return Task.FromResult(new Reply(request.Kind == "bucket" || Volatile.Read(ref present) == 1 ? 200 : 404));
        });
        var run = RunAsync(peer, new StorageExpectation(true, null, null, null, null, null, null), retry: true,
            onAttempt: attempt =>
            {
                if (attempt.Verdict == Verdict.Fail && peer.Requests.Any(x => x.Kind == "bucket"))
                    confirmedMissing.TrySetResult();
            });
        await confirmedMissing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var http = new HttpClient();
        using var put = await http.PutAsync(peer.Url + "/reports/object", new ByteArrayContent(Array.Empty<byte>()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var result = await run;
        Trace(peer, result);

        Assert.Equal(Verdict.Pass, result.Outcome.Verdict);
        Assert.NotEmpty(result.Attempts);
        Assert.Equal(Verdict.Fail, result.Attempts[0].Verdict);
        Assert.Equal(Verdict.Pass, result.Attempts[^1].Verdict);
        Assert.Equal(result.Attempts.Count, result.Live.Attempts.Count);
        Assert.Equal(result.Attempts, result.Live.Attempts);
        Assert.Equal("HEAD object", peer.Requests[0].Method + " " + peer.Requests[0].Kind);
        Assert.Equal("HEAD bucket", peer.Requests[1].Method + " " + peer.Requests[1].Kind);
        Assert.Contains(peer.Requests, x => x.Method == "PUT");
    }

    private void Trace(S3Peer peer, RunResult result) => _output.WriteLine(JsonSerializer.Serialize(new
    {
        requests = peer.Requests,
        verdict = result.Outcome.Verdict.ToString(),
        observation = result.Outcome.Observation,
        attempts = result.Attempts,
        live = result.Live,
    }));

    private static async Task<RunResult> RunAsync(S3Peer peer, StorageExpectation expectation,
        long? timeoutMs = null, bool retry = false, ISecretResolver? resolver = null, Action<AttemptRecord>? onAttempt = null)
    {
        var invocation = PrepareRun(peer, expectation, timeoutMs, retry, resolver, onAttempt);
        await RoslynScriptCompiler.RunIsolatedAsync(invocation.Compiled, invocation.Globals);
        var safe = CsxFragment.SanitiseId(StepId);
        var outcome = Assert.IsType<StepOutcome>(invocation.Vars[VarKeys.Outcome(safe)]);
        var attempts = invocation.Vars.TryGetValue(VarKeys.Attempts(safe), out var value)
            ? Assert.IsType<List<AttemptRecord>>(value) : new List<AttemptRecord>();
        return new RunResult(outcome, attempts, invocation.Live);
    }

    private static Invocation PrepareRun(S3Peer peer, StorageExpectation expectation,
        long? timeoutMs = null, bool retry = false, ISecretResolver? resolver = null,
        Action<AttemptRecord>? onAttempt = null, string tail = "",
        StorageExpectation? warmupExpectation = null, Action<StepOutcome?>? onWarmupCompleted = null)
    {
        var provider = new StorageAssertS3Provider();
        var plans = new List<StepCompilePlan>();
        if (warmupExpectation is not null)
        {
            var setup = provider.Emit(new StorageAssertS3Model("uploads", "reports", "object", warmupExpectation), new CompileContext(WarmupStepId));
            plans.Add(new StepCompilePlan(WarmupStepId, setup, false, null, null));
        }
        var fragment = provider.Emit(new StorageAssertS3Model("uploads", "reports", "object", expectation), new CompileContext());
        plans.Add(new StepCompilePlan(StepId, fragment, retry, timeoutMs ?? (retry ? 5000 : null), retry ? 100 : null));
        var source = CsxAssembler.Assemble(plans).CsxSource + "\n" + tail;
        var references = new[]
        {
            typeof(Amazon.S3.AmazonS3Client).Assembly.Location,
            typeof(Amazon.Runtime.BasicAWSCredentials).Assembly.Location,
            typeof(Json.Path.JsonPath).Assembly.Location,
            typeof(JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(HttpStatusCode).Assembly.Location,
            typeof(SHA256).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(source, additionalReferencePaths: references);
        var vars = new Dictionary<string, object?>
        {
            [VarKeys.Connection("uploads")] = "ServiceURL=" + peer.Url + ";AccessKey=access-canary;SecretKey=credential-canary",
        };
        var live = new LiveSink(onAttempt, onWarmupCompleted);
        ISecretAccessor secrets = resolver is null ? NullSecretAccessor.Instance : new SecretAccessor(new SecretSourceCatalog(new[] { resolver }));
        var globals = new ScriptGlobalVariables(vars, new Dictionary<string, object>(), secrets,
            NullWebhookCaptureAccessor.Instance, NullTraceCaptureAccessor.Instance, live);
        return new Invocation(compiled, globals, vars, live);
    }

    private sealed record Invocation(CompiledScript Compiled, ScriptGlobalVariables Globals,
        Dictionary<string, object?> Vars, LiveSink Live);

    private sealed record RunResult(StepOutcome Outcome, List<AttemptRecord> Attempts, LiveSink Live);

    private sealed class LiveSink : IStepEventSink
    {
        private readonly Action<AttemptRecord>? _onAttempt;
        private readonly Action<StepOutcome?>? _onWarmupCompleted;
        public LiveSink(Action<AttemptRecord>? onAttempt, Action<StepOutcome?>? onWarmupCompleted)
            => (_onAttempt, _onWarmupCompleted) = (onAttempt, onWarmupCompleted);
        public Dictionary<string, StepOutcome?> Completions { get; } = new();
        public List<AttemptRecord> Attempts { get; } = new();
        public StepOutcome? Completed { get; private set; }
        public int CompletionCount { get; private set; }
        public void OnStepStarted(string stepId) { }
        public void OnStepAttempt(string stepId, AttemptRecord attempt)
        {
            Attempts.Add(attempt);
            _onAttempt?.Invoke(attempt);
        }
        public void OnStepCompleted(string stepId, StepOutcome? outcome, string? captureStatus)
        {
            CompletionCount++;
            Completed = outcome;
            Completions.Add(stepId, outcome);
            if (stepId == WarmupStepId) _onWarmupCompleted?.Invoke(outcome);
        }
    }

    private sealed class CompileContext : ICompileContext
    {
        public CompileContext(string stepId = StorageAssertS3ProtocolTests.StepId) => StepId = stepId;
        public string SuiteDirectory => Directory.GetCurrentDirectory();
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
    }

    private sealed class CanaryResolver : ISecretResolver, IDisposable
    {
        private readonly string _environmentName = "VOUCHFX_S3_CANARY_" + Guid.NewGuid().ToString("N");
        public CanaryResolver(string value) => Environment.SetEnvironmentVariable(_environmentName, value);
        public int Calls { get; private set; }
        public string Source => "test";
        public SecretString Resolve(string path)
        {
            Calls++;
            return new EnvironmentSecretResolver().Resolve(_environmentName);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_environmentName, null);
    }

    private sealed record Request(string Method, string Kind, int Status, long BodyLength);
    private sealed record Reply(int Status, byte[]? Body = null, long? HeadLength = null,
        string ContentType = "application/octet-stream", string? Metadata = null,
        bool HoldHeaders = false, bool HoldBody = false, bool DropConnection = false);

    private sealed class S3Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<Request, Task<Reply>> _reply;
        private readonly ConcurrentQueue<Request> _requests = new();
        private readonly ConcurrentBag<Task> _handlers = new();
        private readonly Task _accept;
        private readonly TaskCompletionSource<string> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        public string Url { get; }
        public Request[] Requests => _requests.ToArray();
        public Task<string> HeldOperation => _held.Task;
        public Task<string> ClientDisconnected => _disconnected.Task;

        public S3Peer(Func<Request, Task<Reply>> reply)
        {
            _reply = reply;
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _handlers.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var headers = new List<byte>();
                    var one = new byte[1];
                    while (headers.Count < 65536)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                        headers.Add(one[0]);
                        if (headers.Count >= 4 && Encoding.ASCII.GetString(headers.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                    }
                    var first = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n", StringSplitOptions.None)[0].Split(' ');
                    var request = new Request(first[0], first[1].TrimEnd('/').Count(c => c == '/') > 1 ? "object" : "bucket", 0, 0);
                    var reply = await _reply(request);
                    _requests.Enqueue(request with { Status = reply.Status, BodyLength = reply.Body?.LongLength ?? 0 });
                    if (reply.DropConnection)
                    {
                        // A parsed status line prevents HttpClient's transparent pre-response retry.
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"), _stop.Token);
                        return;
                    }
                    if (reply.HoldHeaders)
                    {
                        await AwaitClientDisconnectAsync(stream, request);
                        return;
                    }
                    var body = reply.Body ?? Array.Empty<byte>();
                    var length = request.Method == "HEAD" ? reply.HeadLength ?? body.LongLength : body.LongLength;
                    var text = "HTTP/1.1 " + reply.Status + " " + (reply.Status == 200 ? "OK" : "Error") + "\r\nConnection: close\r\nContent-Length: " + length +
                        "\r\nContent-Type: " + reply.ContentType + "\r\nETag: \"" + EntityTag(body) + "\"\r\nx-amz-request-id: protocol-fixture\r\n" +
                        (reply.Metadata is null ? "" : "x-amz-meta-source: " + reply.Metadata + "\r\n") + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token);
                    if (request.Method != "HEAD")
                    {
                        if (reply.HoldBody)
                        {
                            await stream.WriteAsync(body.AsMemory(0, 1), _stop.Token);
                            await stream.FlushAsync(_stop.Token);
                            await AwaitClientDisconnectAsync(stream, request);
                            return;
                        }
                        await stream.WriteAsync(body, _stop.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) { /* SDK cancellation/refusal closes its owned socket. */ }
            }
        }

        private async Task AwaitClientDisconnectAsync(NetworkStream stream, Request request)
        {
            var operation = request.Method + " " + request.Kind;
            _held.TrySetResult(operation);
            try
            {
                var buffer = new byte[1];
                while (await stream.ReadAsync(buffer, _stop.Token) != 0) { }
            }
            catch (IOException) when (!_stop.IsCancellationRequested) { }
            if (!_stop.IsCancellationRequested) _disconnected.TrySetResult(operation);
        }

        // S3 ETags use MD5 as a protocol checksum, never as a security primitive.
#pragma warning disable CA5351
        private static string EntityTag(byte[] body) => Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant();
#pragma warning restore CA5351

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_handlers);
            _stop.Dispose();
        }
    }
}
