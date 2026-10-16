using System.Net;
using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

[CollectionDefinition("Telemetry console diagnostics", DisableParallelization = true)]
public sealed class TelemetryConsoleCollectionDefinition { }

[Collection("Telemetry console diagnostics")]
public sealed class TelemetryRefusalDiagnosticTests
{
    private static readonly string[] Lines = { "payload-secret" };

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(413, true)]
    [InlineData(415, true)]
    [InlineData(408, false)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    [InlineData(200, false)]
    public async Task Refusal_ReportsOnlyFixedDiagnosticOnStderr(int status, bool refused)
    {
        var originalError = Console.Error;
        var originalOutput = Console.Out;
        using var error = new StringWriter();
        using var output = new StringWriter();
        try
        {
            Console.SetError(error);
            Console.SetOut(output);
            using var handler = new FakeHttpHandler().RespondWith((HttpStatusCode)status);
            using var http = new HttpClient(handler);
            var client = new HttpOutboxClient(http, new Uri("https://private-endpoint.test/private?query-secret"), "token-secret");
            var ack = await client.PostBatchAsync(Lines, "idempotency-secret", CancellationToken.None);
            Assert.Equal(refused, ack.PermanentlyRefused);
            Assert.Equal(refused ? "Telemetry: backend permanently refused a batch; it will be discarded." + Environment.NewLine : "", error.ToString());
            Assert.Equal("", output.ToString());
            Assert.Equal("payload-secret", Assert.Single(handler.Requests).Body);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOutput);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenStderr_DoesNotTurnPermanentRefusalIntoRetry(bool disposed)
    {
        var originalError = Console.Error;
        using var error = new BrokenWriter();
        try
        {
            if (disposed) error.Dispose();
            Console.SetError(error);
            using var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.BadRequest);
            using var http = new HttpClient(handler);
            var ack = await new HttpOutboxClient(http, new Uri("https://telemetry.test"), "token")
                .PostBatchAsync(Lines, "key", CancellationToken.None);
            Assert.True(ack.PermanentlyRefused);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private sealed class BrokenWriter : StringWriter
    {
        private bool _disposed;
        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
        public override void WriteLine(string? value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new IOException("private writer failure");
        }
    }
}
