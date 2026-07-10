// SUT configuration surface (point 3) — DOCKER-GATED end-to-end proof.
//
// Proves that ScenarioRunner ALSO stages a container-reachable form of a webhook listener's
// callback URL under "<listener>_container" — with the loopback host rewritten to
// host.docker.internal, port and the unguessable token path segment left untouched — BEFORE
// step 1 runs.  A suite author can therefore hand {cb_container} to a containerised SUT in a
// request body while {cb}/svc::cb keep serving in-process (host-context) callers.
//
// Mirrors Sprint07CapstoneTests' "webhook callback captured" scenario exactly (same
// script.csharp-simulated-SUT-callback approach, same appHostAssemblyName, same real topology):
// step 1 (script.csharp) asserts the container-alias staging BEFORE posting to the listener
// (proving the alias exists at step-1 time, not lazily); step 2 (webhook-listen.http, RETRY)
// captures that POST so the overall scenario still resolves to a clean Pass.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~SutEnvConfigWebhookContainerAlias"
// Excluded from non-docker CI:  dotnet test --filter "requires!=docker"

using System;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Docker-gated proof that the webhook listener's callback URL is staged twice — once as
/// <c>{cb}</c> (loopback, host-context) and once as <c>{cb_container}</c> (host.docker.internal,
/// container-context) — before any step runs (SUT configuration surface, point 3).
/// </summary>
public sealed class SutEnvConfigWebhookContainerAliasDockerTests
{
    private readonly ITestOutputHelper _output;

    public SutEnvConfigWebhookContainerAliasDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>Short name of THIS assembly — it carries the DCP metadata.</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(WebhookListenHttpProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    private const string Yaml = """
        metadata:
          name: sut-env-config-webhook-container-alias
          owner: vouchfx-core
          tags: [sut-env-config, webhook]
          description: A script.csharp step asserts {cb_container} is staged (host.docker.internal, same port/token as {cb}) before posting to the listener; webhook-listen.http RETRY captures the POST.

        steps:
          - id: assert-container-alias-and-trigger
            type: script.csharp
            code: |
              var loopback = (string)Vars["cb"];
              if (!Vars.ContainsKey("cb_container"))
                throw new System.Exception("Vars must contain 'cb_container' before step 1 runs.");
              var containerUrl = (string)Vars["cb_container"];

              var loopbackUri = new System.Uri(loopback);
              var containerUri = new System.Uri(containerUrl);

              if (containerUri.Host != "host.docker.internal")
                throw new System.Exception(
                  "Expected host 'host.docker.internal' in cb_container, got '" + containerUri.Host + "'.");
              if (containerUri.Port != loopbackUri.Port)
                throw new System.Exception(
                  "Port mismatch: cb=" + loopbackUri.Port + " cb_container=" + containerUri.Port);
              if (containerUri.AbsolutePath != loopbackUri.AbsolutePath)
                throw new System.Exception(
                  "Token path mismatch: cb=" + loopbackUri.AbsolutePath + " cb_container=" + containerUri.AbsolutePath);
              if (containerUri.Scheme != loopbackUri.Scheme)
                throw new System.Exception(
                  "Scheme mismatch: cb=" + loopbackUri.Scheme + " cb_container=" + containerUri.Scheme);

              // Only the plain key is synthesised — mirrors the existing {V} staging idiom,
              // which stages svc::<V> and <V> but NOT a third svc::<V>_container form.
              if (Vars.ContainsKey("svc::cb_container"))
                throw new System.Exception("svc::cb_container must not be staged (only the plain 'cb_container' key is).");

              var orderRef = "ord-ENV1";
              Vars["ref"] = orderRef;
              var client = new System.Net.Http.HttpClient();
              try {
                var content = new System.Net.Http.StringContent(
                  "{\"orderRef\":\"" + orderRef + "\"}",
                  System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(loopback + "callbacks/" + orderRef, content);
                if ((int)resp.StatusCode >= 400)
                  throw new System.Exception("Callback POST failed: " + (int)resp.StatusCode);
              } finally { client.Dispose(); }

          - id: await-callback
            type: webhook-listen.http
            listener: cb
            verifyMode: RETRY
            timeout: 30s
            match:
              method: POST
              path: "/callbacks/{ref}"
        """;

    [Fact]
    [Trait("requires", "docker")]
    public async Task ContainerAlias_StagedBeforeStep1_HostRewrittenPortAndTokenPreserved_Pass()
    {
        var sw = new System.IO.StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: Yaml,
            scenarioName: "sut-env-config-webhook-container-alias",
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine("=== Terminal output ===");
        _output.WriteLine(rendered);
        _output.WriteLine($"Verdict: {verdict}");

        // Pass proves every 'throw' in the script above was skipped: cb_container was staged,
        // pre-step-1, with host.docker.internal substituted and the port/token/scheme preserved.
        Assert.Equal(Verdict.Pass, verdict);
        Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
    }
}
