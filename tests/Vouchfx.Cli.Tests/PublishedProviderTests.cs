using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Vouchfx.Cli.Watch;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class PublishedProviderTests
{
    [Fact]
    public void Published_provider_is_shared_by_catalogue_schema_scaffold_and_planner()
    {
        var directory = ProviderDirectoryTests.Temp();
        using var scope = new ProviderRegistryScope(ProviderDirectoryScanner.Load(new[] { CopyPublishedProvider(directory) }));
        var catalogue = new StringWriter();
        Assert.Equal(0, ListCommand.Execute(true, catalogue, new StringWriter()));
        using var json = JsonDocument.Parse(catalogue.ToString());
        var rpc = json.RootElement.GetProperty("stepTypes").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "rpc.json-rpc");
        Assert.Equal("community", rpc.GetProperty("tier").GetString());
        var schema = new StringWriter();
        Assert.Equal(0, SchemaCommand.Execute(null, schema, new StringWriter()));
        Assert.Contains("rpc.json-rpc", schema.ToString());
        var intent = Path.Combine(directory, "intent.json");
        File.WriteAllText(intent, "{\"steps\":[{\"id\":\"rpc\",\"type\":\"rpc.json-rpc\"}]}");
        var scaffold = new StringWriter(); var error = new StringWriter();
        Assert.True(ScaffoldCommand.Execute(intent, null, scaffold, error) == 0, error.ToString());
        Assert.Contains("rpc.json-rpc", scaffold.ToString());
        var path = Path.Combine(directory, "rpc.e2e.yaml");
        File.WriteAllText(path, "steps:\n  - id: rpc\n    type: rpc.json-rpc\n    url: http://127.0.0.1:1/\n    method: sum\n");
        var plan = new StringWriter();
        Assert.Equal(0, PlanCommand.Execute(directory, null, true, null, false,
            Vouchfx.Engine.Planning.PlanThresholds.Defaults, plan, error));
        Assert.DoesNotContain("unknown step type", plan.ToString());
        Assert.True(ProviderRegistryFactory.BuildRegistry().TryGet("rpc.json-rpc", out _));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 2)]
    public async Task Published_jsonrpc_binary_executes_capture_through_run_and_watch(bool isolated, int? parallel)
    {
        var directory = ProviderDirectoryTests.Temp(); var providers = CopyPublishedProvider(directory);
        using var scope = new ProviderRegistryScope(ProviderDirectoryScanner.Load(new[] { providers }));
        using var compiler = isolated ? new ScriptCompilerScope(CompileWorkerMode.Compile) : null;
        using var listener = new HttpListener();
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var server = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                using var json = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellation.Token);
                var id = json.RootElement.GetProperty("id").GetRawText();
                var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"sum\":42}}");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes, cancellation.Token); context.Response.Close();
            }
        }, cancellation.Token);
        var yaml = $$"""
            environment:
              source: { driver: test-owned, config: ./environment.yaml }
            steps:
              - id: rpc-call
                type: rpc.json-rpc
                url: http://127.0.0.1:{{port}}/
                method: sum
                expect:
                  result:
                    - path: $.sum
                      value: 42
                capture:
                  answer: $.result.sum
              - id: check-capture
                type: script.csharp
                code: 'if (Vars["answer"].ToString() != "42") throw new Exception("missing external capture");'
            """;
        var path = Path.Combine(directory, "published.e2e.yaml"); await File.WriteAllTextAsync(path, yaml);
        await File.WriteAllTextAsync(Path.Combine(directory, "environment.yaml"), "test-owned");
        var driver = new AuthoringRefusalExitTests.TestDriver { Policy = EnvironmentStatePolicy.FreshOwned };
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var output = new StringWriter();
        try
        {
            var exit = await RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel, watch: false,
                failOnEnvironmentError: true, failOnInconclusive: true, htmlReportPath: null, junitReportPath: null,
                eventsReportPath: null, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
                cancellation.Token, environmentDrivers: drivers);
            Assert.True(exit == 0, output.ToString());
            Assert.Contains("rpc-call", output.ToString());
            await using var watch = WatchRunner.CreateSession(path, ProviderRegistryFactory.BuildRegistry(), output,
                "missing-no-dcp-host", new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(), environmentDrivers: drivers);
            await watch.OnChangeAsync(yaml, cancellation.Token);
            await watch.OnChangeAsync(yaml, cancellation.Token);
            Assert.DoesNotContain("missing external capture", output.ToString());
            Assert.Equal(3, driver.StartCount);
            Assert.All(driver.Leases, lease => Assert.Equal(1, lease.DisposeCount));
        }
        finally
        {
            cancellation.Cancel(); listener.Stop();
            try { await server; } catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException) { }
        }
    }

    [Fact]
    public async Task Published_provider_real_cli_validation_including_compile_worker_succeeds()
    {
        var directory = ProviderDirectoryTests.Temp(); var providers = CopyPublishedProvider(directory);
        var path = Path.Combine(directory, "rpc.e2e.yaml");
        await File.WriteAllTextAsync(path, "steps:\n  - id: rpc\n    type: rpc.json-rpc\n    url: http://127.0.0.1:1/\n    method: sum\n    capture: { answer: '$.result.sum' }\n");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { typeof(ProviderRegistryFactory).Assembly.Location, "validate", path, "--providers", providers, "--no-default-providers", "--isolate-compile", "--json" }) start.ArgumentList.Add(argument);
        start.Environment.Remove("VOUCHFX_PROVIDERS");
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        using var report = JsonDocument.Parse(await stdout);
        Assert.True(report.RootElement.GetProperty("valid").GetBoolean());
    }

    internal static string CopyPublishedProvider(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "providers", "jsonrpc")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "jsonrpc", "Vouchfx.Community.JsonRpc.dll"), Path.Combine(directory, "Vouchfx.Community.JsonRpc.dll"));
        return Path.GetDirectoryName(directory)!;
    }
}
