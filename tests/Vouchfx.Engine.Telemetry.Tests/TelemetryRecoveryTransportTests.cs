using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class TelemetryRecoveryTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Endpoint = new("https://telemetry.vouchfx.test");
    private static readonly string[] OneLine = { "{\"n\":1}" };

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(413)]
    [InlineData(415)]
    public async Task PermanentRefusal_DiscardsWholePoisonBatch_AndProcessesLaterBatch(int status)
    {
        using var paths = new TempPaths();
        var seed = await SeedAsync(paths, 550);
        Assert.True(new OutboxDrainState { ConsecutiveFailures = 3, NextAttemptUtc = Now }.Write(paths.DrainStatePath));
        var handler = new FakeHttpHandler()
            .RespondWith((HttpStatusCode)status)
            .RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var sink = Sink(paths, new HttpOutboxClient(http, Endpoint, "test-token"));

        await sink.SendAsync(SampleEvent());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(string.Join('\n', seed.Take(500)), handler.Requests[0].Body);
        Assert.Empty(await File.ReadAllLinesAsync(paths.OutboxPath));
        Assert.Equal(0, OutboxDrainState.Read(paths.DrainStatePath).ConsecutiveFailures);
    }

    [Theory]
    [InlineData(200, true, false, 1)]
    [InlineData(204, true, false, 1)]
    [InlineData(299, true, false, 1)]
    [InlineData(300, false, true, 0)]
    [InlineData(399, false, true, 0)]
    [InlineData(407, false, true, 0)]
    [InlineData(409, false, true, 0)]
    [InlineData(428, false, true, 0)]
    [InlineData(430, false, true, 0)]
    [InlineData(499, false, true, 0)]
    [InlineData(599, false, false, 0)]
    [InlineData(600, false, true, 0)]
    [InlineData(400, false, true, 0)]
    [InlineData(401, false, true, 0)]
    [InlineData(413, false, true, 0)]
    [InlineData(415, false, true, 0)]
    [InlineData(408, false, false, 0)]
    [InlineData(429, false, false, 0)]
    [InlineData(500, false, false, 0)]
    [InlineData(503, false, false, 0)]
    public async Task BatchDisposition_OnlyTransientStatusesAreRetryable(
        int status, bool delivered, bool refused, int accepted)
    {
        var handler = new FakeHttpHandler().RespondWith((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var ack = await new HttpOutboxClient(http, Endpoint, "test-token")
            .PostBatchAsync(OneLine, "stable-key", CancellationToken.None);

        Assert.Equal(delivered, ack.Delivered);
        Assert.Equal(refused, ack.PermanentlyRefused);
        Assert.Equal(accepted, ack.AcceptedCount);
    }

    [Fact]
    public void ExistingTwoArgumentConstructorAndDeconstruction_DefaultToRetryable()
    {
        var legacy = new DrainAck(false, 0);
        var (delivered, count) = legacy;
        Assert.False(delivered);
        Assert.Equal(0, count);
        Assert.False(legacy.PermanentlyRefused);
        Assert.False(DrainAck.NotDelivered.PermanentlyRefused);
        Assert.Equal(2, typeof(DrainAck).GetMethod("Deconstruct")!.GetParameters().Length);
    }

    [Fact]
    public async Task RefusedThen503_KeepsOnlyRetryableRemainder_WithExactRetryBytesAndKey()
    {
        using var paths = new TempPaths();
        var seed = await SeedAsync(paths, 1050);
        var handler = new FakeHttpHandler()
            .RespondWith(HttpStatusCode.BadRequest)
            .RespondWith(HttpStatusCode.ServiceUnavailable)
            .RespondWith(HttpStatusCode.OK)
            .RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var now = Now;
        var sink = new DrainingTelemetrySink(new LocalFileTelemetrySink(paths),
            new HttpOutboxClient(http, Endpoint, "test-token"), paths,
            OutboxCap.Default, () => now, () => 0.0);

        await sink.SendAsync(SampleEvent());

        Assert.Equal(2, handler.Requests.Count);
        var remainder = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(seed.Skip(500), remainder.Take(550));
        Assert.Equal(551, remainder.Length);
        Assert.Equal(1, OutboxDrainState.Read(paths.DrainStatePath).ConsecutiveFailures);
        var retryBody = string.Join('\n', seed.Skip(500).Take(500));
        var retryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(retryBody))).ToLowerInvariant();
        Assert.Equal(retryBody, handler.Requests[1].Body);
        Assert.Equal(retryKey, handler.Requests[1].IdempotencyKey);

        now = Now.AddDays(1);
        await sink.SendAsync(SampleEvent());

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(handler.Requests[1].Body, handler.Requests[2].Body);
        Assert.Equal(handler.Requests[1].IdempotencyKey, handler.Requests[2].IdempotencyKey);
        Assert.Empty(await File.ReadAllLinesAsync(paths.OutboxPath));
        Assert.Equal(0, OutboxDrainState.Read(paths.DrainStatePath).ConsecutiveFailures);
    }

    [Fact]
    public async Task RefusedThenBudgetExpiry_PersistsProgress_WithoutAdvancingBackoff()
    {
        using var paths = new TempPaths();
        var seed = await SeedAsync(paths, 550);
        Assert.True(new OutboxDrainState { ConsecutiveFailures = 3, NextAttemptUtc = Now }.Write(paths.DrainStatePath));
        Assert.Equal(3, OutboxDrainState.Read(paths.DrainStatePath).ConsecutiveFailures);
        var client = new RefuseThenBlockClient();
        var sink = new DrainingTelemetrySink(new LocalFileTelemetrySink(paths), client, paths,
            OutboxCap.Default, () => Now, () => 0.0, drainBudget: TimeSpan.FromMilliseconds(100));

        await sink.SendAsync(SampleEvent());

        Assert.Equal(2, client.Attempts);
        var remainder = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(seed.Skip(500), remainder.Take(50));
        Assert.Equal(51, remainder.Length);
        Assert.Equal(3, OutboxDrainState.Read(paths.DrainStatePath).ConsecutiveFailures);
    }

    [Fact]
    public async Task CallerCancelAfterRefusal_PreservesSnapshot_AndPropagatesCallerToken()
    {
        using var paths = new TempPaths();
        var seed = await SeedAsync(paths, 550);
        Assert.True(new OutboxDrainState { ConsecutiveFailures = 3, NextAttemptUtc = Now }.Write(paths.DrainStatePath));
        Assert.Equal(3, OutboxDrainState.Read(paths.DrainStatePath).ConsecutiveFailures);
        using var cancellation = new CancellationTokenSource();
        var client = new RefuseThenBlockClient(cancellation);
        var sink = Sink(paths, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sink.SendAsync(SampleEvent(), cancellation.Token));

        Assert.Equal(2, client.Attempts);
        var remaining = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(seed, remaining.Take(550));
        Assert.Equal(551, remaining.Length);
        Assert.Equal(3, OutboxDrainState.Read(paths.DrainStatePath).ConsecutiveFailures);
    }


    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(413)]
    [InlineData(415)]
    public async Task RefusalAndForget_NeverReadResponseBody_AndForgetRemainsFailSilent(int status)
    {
        using var handler = new RefusalHandler((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var transport = new HttpOutboxClient(http, Endpoint, "test-token");

        var ack = await transport.PostBatchAsync(OneLine, "stable-key", CancellationToken.None);
        Assert.False(ack.Delivered);
        Assert.Equal(0, ack.AcceptedCount);
        Assert.True(ack.PermanentlyRefused);
        await transport.ForgetAsync(Guid.Parse("87a236e0-5245-4cac-bdd2-8d5a81f80cb3"), CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, body => Assert.Equal(0, body.ReadAttempts));
    }

    [Fact]
    public async Task BatchBodyByteLimit_StillApplies_WhenRefusedBatchIsDiscarded()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);
        var large = "{\"n\":\"" + new string('é', 280_000) + "\",\"timestamp\":\"" + Now.ToString("o") + "\"}";
        var seed = new[] { large, large };
        await File.WriteAllLinesAsync(paths.OutboxPath, seed);
        var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.BadRequest).RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        await Sink(paths, new HttpOutboxClient(http, Endpoint, "test-token")).SendAsync(SampleEvent());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(large, handler.Requests[0].Body);
        Assert.StartsWith(large + "\n", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.All(handler.Requests, request =>
            Assert.True(Encoding.UTF8.GetByteCount(request.Body!) <= DrainingTelemetrySink.MaxBatchBytes));
        Assert.Empty(await File.ReadAllLinesAsync(paths.OutboxPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpCancellation_PreservesCallerVersusAttemptTimeoutSemantics(bool callerCancels)
    {
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        var client = new HttpOutboxClient(http, Endpoint, "token",
            callerCancels ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(20));
        var send = client.PostBatchAsync(OneLine, "key", caller.Token);
        await handler.Started.Task;
        if (callerCancels)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        }
        else
        {
            var ack = await send;
            Assert.Equal(DrainAck.NotDelivered, ack);
        }
        Assert.Equal(1, handler.Attempts);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RefusalHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public RefusalHandler(HttpStatusCode status) => _status = status;
        public List<UnreadResponseBody> Bodies { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = new UnreadResponseBody();
            Bodies.Add(body);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = body });
        }
    }

    private sealed class UnreadResponseBody : HttpContent
    {
        public int ReadAttempts { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempts++;
            throw new IOException("synthetic private response body must never be read");
        }
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static DrainingTelemetrySink Sink(ITelemetryPaths paths, IOutboxHttpClient http) =>
        new(new LocalFileTelemetrySink(paths), http, paths, OutboxCap.Default, () => Now, () => 0.0);

    private static async Task<string[]> SeedAsync(TempPaths paths, int count)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);
        var seed = Enumerable.Range(0, count)
            .Select(i => i == 250 ? "{broken-poison" : $"{{\"n\":{i},\"timestamp\":\"{Now:o}\"}}")
            .ToArray();
        await File.WriteAllLinesAsync(paths.OutboxPath, seed);
        return seed;
    }

    private sealed class RefuseThenBlockClient : IOutboxHttpClient
    {
        private readonly CancellationTokenSource? _caller;
        public RefuseThenBlockClient(CancellationTokenSource? caller = null) => _caller = caller;
        public int Attempts { get; private set; }
        public async Task<DrainAck> PostBatchAsync(
            IReadOnlyList<string> ndjsonLines, string idempotencyKey, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1) return DrainAck.PermanentRefusal;
            _caller?.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DrainAck.NotDelivered;
        }
        public Task ForgetAsync(Guid installId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static TelemetryEvent SampleEvent() => new()
    {
        SchemaVersion = TelemetryEventBuilder.CurrentSchemaVersion,
        Timestamp = Now,
        InstallId = Guid.Parse("87a236e0-5245-4cac-bdd2-8d5a81f80cb3"),
        ToolVersion = "test",
        EngineVersion = "test",
        DotnetVersion = "test",
        RunCount = 1,
        ScenarioCount = 0,
        StepVerdicts = new TelemetryVerdictCounts(),
        ScenarioVerdicts = new TelemetryVerdictCounts(),
        StepFamilies = new Dictionary<string, int>(),
        StepProviders = new Dictionary<string, int>(),
        StartupMs = 0,
        TimeToFirstTestMs = 0,
        SkippedEventLines = 0,
    };
}
