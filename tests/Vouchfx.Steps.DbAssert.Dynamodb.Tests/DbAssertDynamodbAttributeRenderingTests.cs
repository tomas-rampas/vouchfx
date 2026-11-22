// Pins how the emitted db-assert.dynamodb helper renders each DynamoDB attribute kind for an
// expect.item comparison. Each case compiles and runs the real emitted script against a loopback
// fake that answers GetItem with a hand-written wire response, so the text asserted here is what
// an author sees in the mismatch diff (the `actual` cell) whichever AWSSDK major is referenced.
//
// Rendering contract (unchanged since the provider was written): S as-is; N as the raw stored
// number string; BOOL as "true"/"false"; every other kind (B, NULL, SS, NS, BS, L, M, empty L,
// empty M) and a missing attribute as the literal "null".
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Dynamodb.Tests;

public sealed class DbAssertDynamodbAttributeRenderingTests
{
    private const string StepId = "dyn-render";
    private const string Field = "f";

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
        public string SuiteDirectory => Directory.GetCurrentDirectory();
        public string StepId => DbAssertDynamodbAttributeRenderingTests.StepId;
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    public static TheoryData<string, string> Kinds => new()
    {
        { "{\"S\":\"abc\"}", "abc" },
        { "{\"S\":\"\"}", "" },
        { "{\"N\":\"42\"}", "42" },
        { "{\"N\":\"42.50\"}", "42.50" },
        { "{\"B\":\"AQID\"}", "null" },
        { "{\"BOOL\":true}", "true" },
        { "{\"BOOL\":false}", "false" },
        { "{\"NULL\":true}", "null" },
        { "{\"SS\":[\"a\",\"b\"]}", "null" },
        { "{\"NS\":[\"1\",\"2\"]}", "null" },
        { "{\"BS\":[\"AQID\"]}", "null" },
        { "{\"L\":[{\"S\":\"a\"},{\"N\":\"1\"}]}", "null" },
        { "{\"M\":{\"k\":{\"S\":\"v\"}}}", "null" },
        { "{\"L\":[]}", "null" },
        { "{\"M\":{}}", "null" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Attribute_RendersAsPinnedText(string attributeJson, string expectedActual)
    {
        var outcome = await RunAsync("{\"pk\":{\"S\":\"1\"},\"" + Field + "\":" + attributeJson + "}");

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal(expectedActual, ActualCell(outcome));
    }

    [Fact]
    public async Task MissingAttribute_RendersAsNull()
    {
        var outcome = await RunAsync("{\"pk\":{\"S\":\"1\"}}");

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("null", ActualCell(outcome));
    }

    [Theory]
    [InlineData("{\"S\":\"abc\"}", "abc")]
    [InlineData("{\"N\":\"42\"}", "42")]
    [InlineData("{\"BOOL\":true}", "true")]
    [InlineData("{\"BOOL\":false}", "false")]
    public async Task MatchingAttribute_Passes(string attributeJson, string expected)
    {
        var outcome = await RunAsync("{\"pk\":{\"S\":\"1\"},\"" + Field + "\":" + attributeJson + "}", expected);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal("{\"exists\":true}", outcome.Observation);
    }

    [Fact]
    public async Task ItemWithNoAttributes_IsReportedAbsent()
    {
        // An absent item: the SDK reports no Item (null under AWSSDK 4, empty under 3.7).
        var outcome = await RunAsync(itemJson: null);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("{\"exists\":false}", outcome.Observation);
    }

    [Fact]
    public async Task DroppedConnection_IsOneGetItemRequestAndAnEnvironmentError()
    {
        // AWSSDK 4 retries a dropped connection up to MaxStaleConnectionRetries (default 10)
        // times regardless of MaxErrorRetry; the helper sets it to 0, so exactly one request
        // reaches the peer and the engine-owned retry runner keeps the polling cadence.
        await using var peer = new GetItemPeer("{}", dropConnection: true);
        var model = new DbAssertDynamodbModel("orders", "Orders", "{\"pk\":\"1\"}", new DynamoExpectation(true, null));
        var fragment = new DbAssertDynamodbProvider().Emit(model, new StubCompileContext());
        var compiled = RoslynScriptCompiler.CompileOnce(
            CsxAssembler.Assemble(new[] { (StepId, fragment) }).CsxSource, additionalReferencePaths: s_additionalRefs);
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("orders")] = "ServiceURL=" + peer.Url + ";AccessKey=local;SecretKey=local",
        };

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars));

        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(CsxFragment.SanitiseId(StepId))]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.Equal(1, peer.RequestCount);
    }

    private static string ActualCell(StepOutcome outcome)
    {
        using var doc = JsonDocument.Parse(outcome.Observation!);
        Assert.Equal(Field, doc.RootElement.GetProperty("field").GetString());
        return doc.RootElement.GetProperty("actual").GetString()!;
    }

    private static async Task<StepOutcome> RunAsync(string? itemJson, string expected = "never-matches")
    {
        await using var peer = new GetItemPeer(itemJson is null ? "{}" : "{\"Item\":" + itemJson + "}");
        var item = new Dictionary<string, string>(StringComparer.Ordinal) { [Field] = expected };
        var model = new DbAssertDynamodbModel("orders", "Orders", "{\"pk\":\"1\"}", new DynamoExpectation(true, item));
        var fragment = new DbAssertDynamodbProvider().Emit(model, new StubCompileContext());
        var assembled = CsxAssembler.Assemble(new[] { (StepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("orders")] = "ServiceURL=" + peer.Url + ";AccessKey=local;SecretKey=local",
        };
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars));
        return Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(CsxFragment.SanitiseId(StepId))]);
    }

    /// <summary>Answers every request with one fixed DynamoDB JSON body.</summary>
    private sealed class GetItemPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _body;
        private readonly bool _dropConnection;
        private readonly Task _accept;
        private int _requestCount;

        public string Url { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public GetItemPeer(string body, bool dropConnection = false)
        {
            _dropConnection = dropConnection;
            _body = Encoding.UTF8.GetBytes(body);
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
                    _ = HandleAsync(client);
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
                    var seen = new List<byte>();
                    var one = new byte[1];
                    while (seen.Count < 65536)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                        seen.Add(one[0]);
                        if (seen.Count >= 4 && Encoding.ASCII.GetString(seen.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                    }

                    Interlocked.Increment(ref _requestCount);

                    // Drain the request body so the client is never reset mid-write.
                    var head = Encoding.ASCII.GetString(seen.ToArray());
                    var marker = "Content-Length:";
                    var at = head.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        var end = head.IndexOf('\r', at);
                        var length = int.Parse(head.AsSpan(at + marker.Length, end - at - marker.Length).Trim(),
                            System.Globalization.CultureInfo.InvariantCulture);
                        var buffer = new byte[length];
                        var read = 0;
                        while (read < length)
                        {
                            var n = await stream.ReadAsync(buffer.AsMemory(read), _stop.Token);
                            if (n == 0) return;
                            read += n;
                        }
                    }

                    if (_dropConnection)
                    {
                        // A parsed status line stops HttpClient's own transparent pre-response retry.
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"), _stop.Token);
                        return;
                    }

                    var header = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/x-amz-json-1.0\r\n" +
                        "x-amzn-RequestId: fixture\r\nContent-Length: " + _body.Length + "\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                    await stream.WriteAsync(_body, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            _stop.Dispose();
        }
    }
}
