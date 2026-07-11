// Vouchfx.Engine.Orchestration.Tests — mail-expect.smtp Docker integration tests.
//
// Exercises the full topology path for the mail-expect.smtp provider with a live
// axllent/mailpit container.  Tests in this file carry [Trait("requires","docker")]
// and are excluded from the no-docker CI filter.
//
// Test flow:
//   1. Start a Mailpit dependency via SuiteTopology (EnvironmentMapper maps "mailpit").
//   2. Discover the Mailpit HTTP API URL from DiscoveredServices.
//   3. Discover the Mailpit SMTP URL from DiscoveredServices (staged under svc::mp-smtp).
//   4. Send a test email via raw TCP SMTP (SmtpClient is deprecated; TreatWarningsAsErrors=true
//      globally prevents its use — §Directory.Build.props).
//   5. Run a mail-expect.smtp step via ScenarioRunner and assert Pass.
//   6. Cleanup — SuiteTopology.DisposeAsync tears down the container (§4.5).
//
// The raw TCP SMTP send keeps this test self-contained (no 3rd-party dependency).
// The SMTP protocol conversation is minimal: EHLO / MAIL FROM / RCPT TO / DATA / QUIT.
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.MailExpect.Smtp;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated integration tests for the <c>mail-expect.smtp</c> Core provider.
/// Requires a reachable Docker daemon (axllent/mailpit:v1.21 — the pinned default).
/// </summary>
[Trait("requires", "docker")]
public sealed class MailExpectSmtpDockerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public MailExpectSmtpDockerTests(ITestOutputHelper output) => _output = output;

    // ── Topology constants ────────────────────────────────────────────────────────

    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";
    private const string DepName = "mp";

    private SuiteTopology? _suite;
    private string? _httpBaseUrl;
    private string? _smtpUrl;

    // ── Lifecycle ─────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var deps = new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
        {
            [DepName] = new DependencySpec(Type: "mailpit", Version: string.Empty, Extra: null),
        };
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(),
            Dependencies: deps,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        _suite = await SuiteTopology.StartAsync(
            env,
            AppHostAssemblyName,
            startupTimeout: TimeSpan.FromSeconds(120));

        // The HTTP API URL is staged at conn::mp (dependency name "mp" is in DependencyNames).
        _httpBaseUrl = _suite.DiscoveredServices.TryGetValue(DepName, out var v) && v is string s
            ? s
            : throw new InvalidOperationException(
                $"Mailpit HTTP URL not found in DiscoveredServices for key '{DepName}'. " +
                $"Available: [{string.Join(", ", _suite.DiscoveredServices.Keys)}]");

        // The SMTP URL is staged at svc::mp-smtp (not in DependencyNames).
        var smtpKey = DepName + "-smtp";
        _smtpUrl = _suite.DiscoveredServices.TryGetValue(smtpKey, out var sv) && sv is string ss
            ? ss
            : throw new InvalidOperationException(
                $"Mailpit SMTP URL not found in DiscoveredServices for key '{smtpKey}'. " +
                $"Available: [{string.Join(", ", _suite.DiscoveredServices.Keys)}]");
    }

    public async Task DisposeAsync()
    {
        if (_suite is not null)
            await _suite.DisposeAsync().ConfigureAwait(false);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a test email to the live Mailpit SMTP port and asserts that a
    /// <c>mail-expect.smtp</c> step with matching criteria yields
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MailExpectSmtp_MatchingMessage_ReturnsPass()
    {
        Assert.NotNull(_smtpUrl);
        Assert.NotNull(_httpBaseUrl);

        // Parse host and port from the SMTP URL (format: http://host:port or tcp://host:port
        // or plain host:port — take the last colon-separated segment as port).
        var (smtpHost, smtpPort) = ParseHostPort(_smtpUrl!);

        const string from = "sender@example.com";
        const string to = "recipient@example.com";
        const string subject = "Integration Test Mail";
        const string body = "Hello from vouchfx mail-expect.smtp!";

        await SendSmtpEmailAsync(smtpHost, smtpPort, from, to, subject, body);

        // Build a mail-expect.smtp scenario (compile once — §5 memory model).
        var provider = new MailExpectSmtpProvider();
        var model = new MailExpectSmtpModel(
            Target: DepName,
            Expect: new MailExpectation(
                Match: new MailMatch(
                    To: to,
                    SubjectContains: "Integration Test"),
                Count: 1));

        var fragment = provider.Emit(model, new StubCompileCtx("mail-check"));
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        // Pass the provider's compile-reference assemblies so Roslyn can resolve
        // System.Net.Http.HttpClient and System.Text.Json.JsonDocument in the helper.
        var refPaths = ((Vouchfx.Sdk.ICompileReferenceContributor)provider)
            .CompileReferenceAssemblies
            .Select(a => a.Location)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        var compiled = Vouchfx.Engine.Compilation.RoslynScriptCompiler.CompileOnce(
            csx, additionalReferencePaths: refPaths);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId("mail-check"));

        // Poll for the message to become visible via Mailpit's HTTP API, mirroring how a
        // real suite would run this step under verifyMode: RETRY (this provider is
        // documented as a RETRY consumer whose emitted helper performs a single idempotent
        // scan and never writes Inconclusive itself — the engine-owned RetryRunner is what
        // re-invokes it). A fixed post-send delay here was flaky under CI's slower runners:
        // a single scan sometimes ran before Mailpit had indexed the just-sent message, and
        // there was no retry to recover — compile once, then re-invoke RunIsolatedAsync (safe
        // to call repeatedly against the same CompiledScript, per its own doc) until Pass or
        // a generous bounded timeout elapses.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        StepOutcome outcome;
        while (true)
        {
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection(DepName)] = _httpBaseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);

            await Vouchfx.Engine.Compilation.RoslynScriptCompiler
                .RunIsolatedAsync(compiled, globals);

            Assert.True(vars.ContainsKey(outcomeKey),
                $"Expected outcome key '{outcomeKey}'. Keys: [{string.Join(", ", vars.Keys)}]");
            outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            if (outcome.Verdict == Verdict.Pass || DateTime.UtcNow >= deadline)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        // Diagnosability on the CI-only failure path (this test was red on every CI run
        // since its introduction while green locally): before asserting, surface what the
        // provider saw and what Mailpit actually holds, so a red run explains itself.
        if (outcome.Verdict != Verdict.Pass)
        {
            _output.WriteLine($"Final outcome: verdict={outcome.Verdict} observation={outcome.Observation}");
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var inbox = await http.GetStringAsync($"{_httpBaseUrl}/api/v1/messages?limit=100");
                _output.WriteLine($"Mailpit inbox ({_httpBaseUrl}): {inbox}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Mailpit inbox fetch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parses "http://host:port", "tcp://host:port", or "host:port" into (host, port).
    /// Uses LastIndexOf(':') to handle IPv6 addresses and arbitrary prefixes.
    /// </summary>
    private static (string Host, int Port) ParseHostPort(string url)
    {
        // Strip scheme if present.
        var hostPort = url;
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
            hostPort = url[(schemeEnd + 3)..];

        var lastColon = hostPort.LastIndexOf(':');
        if (lastColon < 0)
            return (hostPort, 1025);

        var host = hostPort[..lastColon];
        return int.TryParse(hostPort[(lastColon + 1)..], out var port)
            ? (host, port)
            : (hostPort, 1025);
    }

    /// <summary>
    /// Sends a minimal RFC-2821 SMTP message to <paramref name="host"/>:<paramref name="port"/>
    /// using a raw TCP connection.  Uses no deprecated APIs (SmtpClient is deprecated and
    /// TreatWarningsAsErrors=true globally blocks it — Directory.Build.props).
    /// </summary>
    private async Task SendSmtpEmailAsync(
        string host, int port, string from, string to, string subject, string body)
    {
        // The whole conversation is bounded: a stuck/silent server surfaces as a visible
        // failure carrying the transcript so far, never a hung test (the catch below wraps
        // the raw cancellation, which by itself would carry no transcript).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var transcript = new List<string>();

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var writer = new StreamWriter(
                stream,
                Encoding.ASCII,
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
                // SMTP is a CRLF protocol (RFC 5321 §2.3.8). StreamWriter.WriteLineAsync
                // emits Environment.NewLine by default — bare LF on Linux — which is a
                // protocol violation and behaved differently on the Linux CI runner than
                // on local Windows (this suite was CI-red from its first run). Pin CRLF.
                NewLine = "\r\n",
            };
            try
            {
                // Every response code is asserted (2xx/3xx) with the full conversation
                // transcript on failure — a rejected command previously sailed on
                // silently and surfaced only as "0 messages matched" much later.
                async Task<string> ReadReplyAsync(string afterCommand)
                {
                    string? first = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (first is null)
                        throw new InvalidOperationException(
                            $"SMTP server closed the connection after '{afterCommand}'. Transcript: {string.Join(" | ", transcript)}");
                    transcript.Add($"S: {first}");
                    // Consume any multi-line continuation ("250-...").
                    var line = first;
                    while (line.Length > 3 && line[3] == '-')
                    {
                        line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
                            ?? throw new InvalidOperationException(
                                $"SMTP server closed mid multi-line reply after '{afterCommand}'. Transcript: {string.Join(" | ", transcript)}");
                        transcript.Add($"S: {line}");
                    }
                    if (first.Length < 3 || (first[0] != '2' && first[0] != '3'))
                        throw new InvalidOperationException(
                            $"SMTP server rejected '{afterCommand}': {first}. Transcript: {string.Join(" | ", transcript)}");
                    return first;
                }

                async Task SendAsync(string commandOrLine)
                {
                    transcript.Add($"C: {commandOrLine}");
                    await writer.WriteLineAsync(commandOrLine.AsMemory(), cts.Token).ConfigureAwait(false);
                }

                await ReadReplyAsync("<greeting>").ConfigureAwait(false);

                await SendAsync("EHLO vouchfx-test").ConfigureAwait(false);
                await ReadReplyAsync("EHLO").ConfigureAwait(false);

                await SendAsync($"MAIL FROM:<{from}>").ConfigureAwait(false);
                await ReadReplyAsync("MAIL FROM").ConfigureAwait(false);

                await SendAsync($"RCPT TO:<{to}>").ConfigureAwait(false);
                await ReadReplyAsync("RCPT TO").ConfigureAwait(false);

                await SendAsync("DATA").ConfigureAwait(false);
                await ReadReplyAsync("DATA").ConfigureAwait(false);

                await SendAsync($"From: <{from}>").ConfigureAwait(false);
                await SendAsync($"To: <{to}>").ConfigureAwait(false);
                await SendAsync($"Subject: {subject}").ConfigureAwait(false);
                await SendAsync("").ConfigureAwait(false);
                await SendAsync(body).ConfigureAwait(false);
                await SendAsync(".").ConfigureAwait(false);
                await ReadReplyAsync("<end of DATA>").ConfigureAwait(false);

                await SendAsync("QUIT").ConfigureAwait(false);
                // QUIT's 221 is best-effort; the message is already accepted.
                await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);

                _output.WriteLine($"SMTP conversation OK ({transcript.Count} lines).");
            }
            finally
            {
                writer.Dispose();
                reader.Dispose();
            }
        }
        catch (OperationCanceledException oce) when (cts.IsCancellationRequested)
        {
            // The raw cancellation carries no context — wrap it so the CI log shows how
            // far the conversation got before the server went silent.
            throw new InvalidOperationException(
                $"SMTP conversation timed out (60 s bound). Transcript: {string.Join(" | ", transcript)}", oce);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Minimal <see cref="ICompileContext"/> stub for the Docker test emit call.
    /// </summary>
    private sealed class StubCompileCtx : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileCtx(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }
}
