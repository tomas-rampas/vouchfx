using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Cli.Watch;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ManagedKafkaSecurityCliTests
{
    [Theory]
    [InlineData("KAFKA_listeners", "private_alias_canary", "engine-owned")]
    [InlineData("KAFKA.LISTENERS", "private_alias_canary", "engine-owned")]
    [InlineData("KAFKAXlisteners", "private_alias_canary", "engine-owned")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_ssl_client_auth", "private_alias_canary", "engine-owned")]
    [InlineData("KAFKA_listener_name_vouchfxsecure_ssl_client_auth", "private_alias_canary", "engine-owned")]
    [InlineData("KAFKA_LISTENER.NAME.OTHER.SSL.CLIENT.AUTH", "private_alias_canary", "engine-owned")]
    [InlineData("KAFKA_ssl_keystore_type", "private_alias_canary", "file-backed PEM")]
    [InlineData("KAFKA_SSL.KEYSTORE.TYPE", "private_alias_canary", "file-backed PEM")]
    [InlineData("KAFKA_ssl_truststore_type", "private_alias_canary", "file-backed PEM")]
    [InlineData("KAFKA_ssl_keystore_location", "private_alias_canary", "declared")]
    [InlineData("KAFKA_SSL.KEYSTORE.LOCATION", "private_alias_canary", "declared")]
    [InlineData("KAFKA_ssl.truststore_location", "private_alias_canary", "declared")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE", "private_alias_canary", "file-backed PEM")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.keystore.type", "private_alias_canary", "file-backed PEM")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_TRUSTSTORE_TYPE", "private_alias_canary", "file-backed PEM")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_LOCATION", "private_alias_canary", "declared")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.truststore.location", "private_alias_canary", "declared")]
    [InlineData("KAFKAXlistener.name.vouchfxsecure.ssl.keystore.location", "private_alias_canary", "declared")]
    public async Task EffectivePropertyAlias_NormalValidateRunAndWatchPlanRefuseBeforeAcquisition(string key, string value, string reason)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka-alias-cli-").FullName;
        var yaml = ScriptScenario("broker", "tcp", "return;").Replace("steps:",
            $"      env: {{ {key}: '{value}' }}\nsteps:", StringComparison.Ordinal);
        var path = Path.Combine(directory, "alias.e2e.yaml");
        File.WriteAllText(path, yaml);
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(4, ValidateCommand.Execute(path, json: true, output, errors));
        Assert.Empty(errors.ToString());
        using (var report = JsonDocument.Parse(output.ToString()))
            Assert.False(report.RootElement.GetProperty("valid").GetBoolean());
        var starts = 0;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (_, _) =>
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("An effective-property refusal reached acquisition.");
        });
        foreach (var parallel in new int?[] { null, 2 })
        {
            var events = Path.Combine(directory, $"events-{parallel}.jsonl");
            Assert.Equal(4, await RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel, watch: false,
                failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
                eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
                CancellationToken.None, environmentDrivers: registry));
            if (File.Exists(events))
            {
                var text = await File.ReadAllTextAsync(events);
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
                Assert.DoesNotContain("\"type\":\"step-started\"", text, StringComparison.Ordinal);
            }
        }
        var kinds = ProviderRegistryFactory.BuildCoreRegistry();
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), kinds);
        var watch = WatchIterationPlan.Create(ast, yaml, "alias", kinds, "unused-host", directory, registry);
        Assert.True(watch.IsRefused);
        Assert.Contains("environment.dependencies.broker.env", watch.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(reason, watch.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(value, watch.Diagnostic + output, StringComparison.Ordinal);
        await using var session = WatchRunner.CreateSession(path, kinds, output, "unused-host",
            new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(), environmentDrivers: registry);
        await session.OnChangeAsync(yaml, CancellationToken.None);
        Assert.Contains(reason, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Run_DeclaredServerFileBecameUnreadable_UsesNamedMaterialProvenanceBeforeDocker(int? parallel)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-access-").FullName;
        var pem = Path.Combine(directory, "server.pem");
        File.WriteAllText(pem, "synthetic_private_material_canary_443");
        File.WriteAllText(Path.Combine(directory, "access.e2e.yaml"), """
            environment:
              dependencies:
                broker:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: tcp
                    serverArtifacts:
                      - { source: ./server.pem, target: /material/server.pem }
            steps:
              - id: never-executed
                type: script.csharp
                code: 'return;'
            """);
        using var exclusivelyLocked = new FileStream(pem, FileMode.Open, FileAccess.Read, FileShare.None);
        var starts = 0;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (context, cancellationToken) =>
        {
            Interlocked.Increment(ref starts);
            var mapped = EnvironmentMapper.Map(context.Request.Environment, directory);
            mapped.VerifyServerArtifactAccess(cancellationToken);
            throw new InvalidOperationException("Unreadable source passed its real acquisition guard.");
        });
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        Assert.Equal(3, await RunCommand.ExecuteAsync(directory, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            CancellationToken.None, environmentDrivers: registry));
        Assert.Equal(1, starts);
        var eventText = await File.ReadAllTextAsync(events);
        Assert.Contains(RunCommand.SecurityUnconfirmableNotice, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic_private_material_canary_443", output + eventText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"step-started\"", eventText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "broker", true, false, 3)]
    [InlineData(1, "broker", true, false, 3)]
    [InlineData(2, "broker", true, false, 3)]
    [InlineData(null, "other", true, false, 0)]
    [InlineData(1, "other", true, false, 0)]
    [InlineData(2, "other", true, false, 0)]
    [InlineData(null, "broker", false, false, 0)]
    [InlineData(2, "broker", false, false, 0)]
    [InlineData(null, "other", true, true, 3)]
    [InlineData(2, "broker", true, true, 3)]
    public async Task Run_TypedSecuredResourceReadinessFailure_IsNonzeroAndUnrelatedFailureRemainsOptIn(
        int? parallel, string failedResource, bool attributed, bool strict, int expectedExit)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-secured390-cli-").FullName;
        File.WriteAllText(Path.Combine(directory, "failure.e2e.yaml"), """
            environment:
              dependencies:
                broker:
                  type: kafka
                  security: { profile: tls, endpoint: tcp }
                other: { type: redis }
            steps:
              - id: never-executed
                type: script.csharp
                code: 'return;'
            """);
        var starts = 0;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (_, _) =>
        {
            Interlocked.Increment(ref starts);
            throw new OrchestrationException(new OrchestrationErrorInfo(
                OrchestrationErrorKind.HealthGate, failedResource, null, null, "synthetic resource readiness failure"))
            { AcquisitionStage = attributed ? ResourceAcquisitionStage.Readiness : ResourceAcquisitionStage.Unattributed };
        });
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        var exit = await RunCommand.ExecuteAsync(directory, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: strict, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            CancellationToken.None, environmentDrivers: registry);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(1, starts);
        var lines = await File.ReadAllTextAsync(events);
        Assert.DoesNotContain("\"type\":\"step-started\"", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("security-confirmation", lines, StringComparison.Ordinal);
        Assert.Equal(attributed && failedResource == "broker", output.ToString().Contains(RunCommand.SecurityUnconfirmableNotice, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnknownSecuredKafkaEndpoint_IsRefusedByNormalCli()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-cli-red-").FullName;
        var path = Path.Combine(directory, "probe.e2e.yaml");
        File.WriteAllText(path, """
            environment:
              dependencies:
                broker:
                  type: kafka
                  security: { profile: tls, endpoint: misspelled }
            steps:
              - id: never-executed
                type: script.csharp
                code: 'return;'
            """);
        var output = new StringWriter();
        Assert.Equal(4, ValidateCommand.Execute(path, json: true, output, new StringWriter()));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.False(report.RootElement.GetProperty("valid").GetBoolean());
    }

    [Theory]
    [InlineData(1, false, true, 3)]
    [InlineData(2, false, true, 3)]
    [InlineData(1, true, true, 3)]
    [InlineData(2, true, true, 3)]
    [InlineData(1, false, false, 0)]
    [InlineData(2, false, false, 0)]
    public async Task MixedDocuments_KeepNamedFailureSeparateFromSiblingConfirmation(
        int parallel, bool failingSibling, bool attributed, int expectedExit)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-secured390-mixed-").FullName;
        File.WriteAllText(Path.Combine(directory, "a-healthy.e2e.yaml"), ScriptScenario("healthy", "tcp",
            failingSibling ? "throw new Exception(\"expected assertion failure\");" : "return;"));
        File.WriteAllText(Path.Combine(directory, "b-unavailable.e2e.yaml"), ScriptScenario("unavailable", "tcp", "return;"));
        var starts = 0;
        var healthy = new FakeKeptTopology();
        healthy.Services["healthy"] = "localhost:1";
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (context, _) =>
        {
            Interlocked.Increment(ref starts);
            if (context.Request.Environment!.Dependencies!.ContainsKey("unavailable"))
                throw new OrchestrationException(new OrchestrationErrorInfo(
                    OrchestrationErrorKind.HealthGate, "unavailable", null, null, "synthetic own readiness failure"))
                { AcquisitionStage = attributed ? ResourceAcquisitionStage.Readiness : ResourceAcquisitionStage.Unattributed };
            var target = Assert.Single(SecuredTargets.Enumerate(context.Request.Environment));
            // This double proves declaration pairing and folds, never native confirmation.
            healthy.Confirmations = new[] { new SecurityConfirmation(target.Name, target.Kind, "tls", "tcp",
                "localhost:1", "Tls13", false, SecurityConfirmationLevel.AuthenticatedRoundTrip, "test double",
                SecuredTargets.IdentityOf(target)) };
            return Task.FromResult<IKeptTopology>(healthy);
        });
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        Assert.Equal(expectedExit, await RunCommand.ExecuteAsync(directory, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            CancellationToken.None, environmentDrivers: registry));
        Assert.Equal(2, starts);
        Assert.Equal(1, healthy.DisposeCount);
        using var completed = JsonDocument.Parse((await File.ReadAllLinesAsync(events))
            .Single(line => line.Contains("\"type\":\"step-completed\"", StringComparison.Ordinal)));
        Assert.Equal(failingSibling ? "FAIL" : "PASS", completed.RootElement.GetProperty("verdict").GetString());
        // Existing verdict ordering aggregates EnvironmentError above Fail; this slice preserves it.
        Assert.Equal(attributed,
            output.ToString().Contains(RunCommand.SecurityUnconfirmableNotice, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("misspelled", "")]
    [InlineData("tcp", "      env: { KAFKA_ADVERTISED_LISTENERS: 'VOUCHFXSECURE://fixed.invalid:9092' }")]
    public async Task RunAndWatch_PreflightRefusal_NeverAcquiresInlineTopology(string selector, string extra)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-preflight-").FullName;
        var yaml = ScriptScenario("broker", selector, "return;").Replace("steps:", extra + "\nsteps:", StringComparison.Ordinal);
        var path = Path.Combine(directory, "preflight.e2e.yaml");
        File.WriteAllText(path, yaml);
        var starts = 0;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (_, _) =>
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("A pure refused recipe reached acquisition.");
        });
        var output = new StringWriter();
        Assert.Equal(4, await RunCommand.ExecuteAsync(path, SelectionCriteria.None, null, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: null, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            CancellationToken.None, environmentDrivers: registry));
        var kinds = ProviderRegistryFactory.BuildCoreRegistry();
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), kinds);
        var watch = WatchIterationPlan.Create(ast, yaml, "preflight", kinds, "unused-host", directory, registry);
        Assert.True(watch.IsRefused);
        Assert.Contains("environment.dependencies.broker", watch.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(selector == "tcp" ? "engine-owned" : "not configured", watch.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CallerCancellationDuringAcquisition_RemainsCancellationWithoutFalseConfirmation(int? parallel)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-cancel-control-").FullName;
        var path = Path.Combine(directory, "cancel.e2e.yaml");
        File.WriteAllText(path, ScriptScenario("broker", "tcp", "return;"));
        using var cancellation = new CancellationTokenSource();
        var starts = 0;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (_, token) =>
        {
            Interlocked.Increment(ref starts);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Caller cancellation was ignored.");
        });
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        int? returned = null;
        var escaped = await Record.ExceptionAsync(async () => returned = await RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            cancellation.Token, environmentDrivers: registry));
        File.WriteAllText(Path.Combine(directory, "caller-cancellation-outcome.json"), JsonSerializer.Serialize(new
        { parallel, returned, escaped = escaped?.GetType().Name, callerRequested = cancellation.IsCancellationRequested, starts }));
        Assert.IsAssignableFrom<OperationCanceledException>(escaped);
        Assert.Equal(1, starts);
        Assert.DoesNotContain(RunCommand.SecurityUnconfirmableNotice, output.ToString(), StringComparison.Ordinal);
        if (File.Exists(events))
            Assert.DoesNotContain("\"type\":\"step-completed\"", await File.ReadAllTextAsync(events), StringComparison.Ordinal);
        if (parallel is not null)
        {
            Assert.True(File.Exists(events), "Joined parallel cancellation did not retain its archive.");
            Assert.Contains("\"verdict\":\"INCONCLUSIVE\"", await File.ReadAllTextAsync(events), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CallerCancellationAfterPassingSibling_JoinsDisposalAndPreservesBothReports(int parallel)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-cancel-joined-").FullName;
        File.WriteAllText(Path.Combine(directory, "a-pass.e2e.yaml"), ScriptScenario("healthy", "tcp", "return;"));
        File.WriteAllText(Path.Combine(directory, "b-cancel.e2e.yaml"), ScriptScenario("cancelled", "tcp", "return;"));
        var healthy = new FakeKeptTopology();
        healthy.Services["healthy"] = "localhost:1";
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var starts = 0;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), async (context, ct) =>
        {
            Interlocked.Increment(ref starts);
            if (context.Request.Environment!.Dependencies!.ContainsKey("healthy"))
            {
                var target = Assert.Single(SecuredTargets.Enumerate(context.Request.Environment));
                healthy.Confirmations = new[] { new SecurityConfirmation(target.Name, target.Kind, "tls", "tcp", "localhost:1", "Tls13",
                    false, SecurityConfirmationLevel.AuthenticatedRoundTrip, "test double", SecuredTargets.IdentityOf(target)) };
                return healthy;
            }
            while (healthy.DisposeCount == 0) await Task.Delay(10, ct);
            caller.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Caller cancellation was ignored.");
        });
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCommand.ExecuteAsync(directory, SelectionCriteria.None, parallel,
            watch: false, failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            caller.Token, environmentDrivers: registry));
        Assert.Equal(2, starts);
        Assert.Equal(1, healthy.DisposeCount);
        var lines = await File.ReadAllLinesAsync(events);
        var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var scenarios = records.Where(record => record.RootElement.GetProperty("type").GetString() == "scenario-completed").ToArray();
            Assert.Equal(2, scenarios.Length);
            Assert.Contains(scenarios, record => record.RootElement.GetProperty("verdict").GetString() == "PASS");
            Assert.Contains(scenarios, record => record.RootElement.GetProperty("verdict").GetString() == "INCONCLUSIVE");
            var step = Assert.Single(records, record => record.RootElement.GetProperty("type").GetString() == "step-completed");
            Assert.Equal("PASS", step.RootElement.GetProperty("verdict").GetString());
        }
        finally { foreach (var record in records) record.Dispose(); }
        Assert.DoesNotContain(RunCommand.SecurityUnconfirmableNotice, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnrequestedTaskCancellation_PreservesTheExistingNoVerdictPolicy(int? parallel)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-unrequested-cancel-").FullName;
        var path = Path.Combine(directory, "timeout.e2e.yaml");
        File.WriteAllText(path, ScriptScenario("broker", "tcp", "return;"));
        var starts = 0;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (_, _) =>
        {
            Interlocked.Increment(ref starts);
            throw new TaskCanceledException("synthetic transport timeout");
        });
        var output = new StringWriter();
        var code = await RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: null, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            CancellationToken.None, environmentDrivers: registry);
        Assert.Equal(4, code);
        Assert.Equal(1, starts);
        Assert.DoesNotContain(RunCommand.SecurityUnconfirmableNotice, output.ToString(), StringComparison.Ordinal);
        if (parallel is null) Assert.Contains(nameof(TaskCanceledException), output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 3)]
    public void ExistingAggregationAndExitPolicy_RemainUnchanged(bool infrastructureSibling, bool strict, int expected)
    {
        var elevate = typeof(Vouchfx.Engine.Runtime.ScenarioRunner).GetMethod("Elevate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(elevate);
        var aggregate = Assert.IsType<Verdict>(elevate.Invoke(null, new object[]
            { Verdict.Fail, infrastructureSibling ? Verdict.EnvironmentError : Verdict.Pass }));
        Assert.Equal(infrastructureSibling ? Verdict.EnvironmentError : Verdict.Fail, aggregate);
        Assert.Equal(expected, ExitCodes.FromVerdict(aggregate, strict, failOnInconclusive: false));
    }

    private static string ScriptScenario(string broker, string selector, string code) => $$"""
        environment:
          dependencies:
            {{broker}}:
              type: kafka
              security: { profile: tls, endpoint: "{{selector}}" }
        steps:
          - id: local
            type: script.csharp
            code: '{{code}}'
        """;
}
