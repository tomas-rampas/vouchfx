using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.WebhookListen.Http;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>One container-originated callback proves the staged hostname and HTTPS policy.</summary>
public sealed class InboundListenerContainerDockerTests
{
    private readonly ITestOutputHelper _output;

    public InboundListenerContainerDockerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("requires", "docker")]
    public async Task StagedContainerUrl_UsesHttpsAndAcceptsOnlyDeclaredClientIdentity()
    {
        using var bed = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        using var client = X509Certificate2.CreateFromPemFile(
            Path.Combine(bed.SuiteDirectory, "client.pem"),
            Path.Combine(bed.SuiteDirectory, "client-key.pem"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(client.RawData)).ToLowerInvariant();
        var yaml = """
            listeners:
              cb:
                kind: webhook-listener
                security:
                  profile: mtls
                  serverCert: server.pem
                  serverKey: server-key.pem
                  clientCaCert: ca.pem
            steps:
              - id: container-callback
                type: script.csharp
                code: |
                  void Fail(string message) {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(__HOST_DIR__, "callback-error.txt"), message);
                    throw new System.Exception(message);
                  }
                  var local = new System.Uri((string)Vars["cb"]);
                  var container = new System.Uri((string)Vars["cb_container"]);
                  if (local.Scheme != "https" || container.Scheme != "https"
                      || container.Host != "host.docker.internal"
                      || container.Port != local.Port || container.AbsolutePath != local.AbsolutePath)
                    Fail("HTTPS listener URL staging is inconsistent.");
                  var start = new System.Diagnostics.ProcessStartInfo("docker") {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                  };
                  foreach (var arg in new[] {
                    "run", "--rm", "--user", "0", "--add-host", "host.docker.internal:host-gateway",
                    "--mount", "type=bind,src=" + __HOST_DIR__ + ",dst=/certs,readonly",
                    "curlimages/curl:8.10.1", "--fail", "--show-error", "--silent", "--max-time", "30",
                    "--cacert", "/certs/ca.pem", "--cert", "/certs/client.pem",
                    "--key", "/certs/client-key.pem", "--data", "payload",
                    container.ToString() + "from-container"
                  }) start.ArgumentList.Add(arg);
                  using (var process = System.Diagnostics.Process.Start(start)!) {
                    var stderr = process.StandardError.ReadToEndAsync();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(60000)) {
                      process.Kill(true);
                      Fail("Container callback timed out.");
                    }
                    if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stderr, stdout }, 5000))
                      Fail("Container callback output drain timed out.");
                    if (process.ExitCode != 0)
                      Fail("Container callback failed (exit " + process.ExitCode + "): " + stderr.GetAwaiter().GetResult());
                  }
              - id: assert-callback
                type: webhook-listen.http
                listener: cb
                match:
                  path: /from-container
                  peerCertificateSha256: __FINGERPRINT__
            """
            .Replace("__HOST_DIR__", JsonSerializer.Serialize(bed.SuiteDirectory), StringComparison.Ordinal)
            .Replace("__FINGERPRINT__", fingerprint, StringComparison.Ordinal);
        var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // The fixture keeps its private key owner-only (0600 on Linux). The curl image's
        // default UID 100 cannot read the host user's key, so this test-only client uses root.
        var verdict = await ScenarioRunner.RunAsync(yaml, "inbound-container-callback",
            new[] { typeof(ScriptCsharpProvider).Assembly, typeof(WebhookListenHttpProvider).Assembly },
            "Vouchfx.Engine.Runtime.Tests", output, bed.SuiteDirectory, cts.Token);

        _output.WriteLine(output.ToString());
        // Preserve the step exception (including curl stderr) in CI's xUnit/TRX output.
        var diagnosticPath = Path.Combine(bed.SuiteDirectory, "callback-error.txt");
        if (File.Exists(diagnosticPath))
            _output.WriteLine(await File.ReadAllTextAsync(diagnosticPath, cts.Token));
        Assert.Equal(Verdict.Pass, verdict);
    }
}
