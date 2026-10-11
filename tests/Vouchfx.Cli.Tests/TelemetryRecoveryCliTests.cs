using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Telemetry;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class TelemetryRecoveryCliTests
{
    private const string RefusedYaml = """
        steps:
          - id: invalid
            type: http.rest
            target: missing-service
            method: GET
            path: /
        """;

    [Fact]
    public async Task NormalRun_DiscoveryFailure_DoesNotEmitOlderReadableArchive()
    {
        using var temp = new OwnedPaths();
        var suite = Path.Combine(temp.Root, "broken.e2e.yaml");
        await File.WriteAllTextAsync(suite, "steps: [");
        var archive = Path.Combine(temp.Root, "events.jsonl");
        var sentinel = string.Join('\n', OldStream());
        await File.WriteAllTextAsync(archive, sentinel);
        var sink = new RecordingSink();

        var exit = await RunAsync(suite, archive, Hook(temp, sink));

        Assert.Equal(4, exit);
        Assert.Empty(sink.Sent);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(archive));
    }

    [Fact]
    public async Task NormalRun_CurrentArchiveWriteFails_OlderReadableArchiveDoesNotEmit()
    {
        using var temp = new OwnedPaths();
        var suite = Path.Combine(temp.Root, "refused.e2e.yaml");
        await File.WriteAllTextAsync(suite, RefusedYaml);
        var archive = Path.Combine(temp.Root, "events.jsonl");
        var sentinel = string.Join('\n', OldStream());
        await File.WriteAllTextAsync(archive, sentinel);
        var sink = new RecordingSink();
        using var held = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);

        var exit = await RunAsync(suite, archive, Hook(temp, sink));

        Assert.Equal(4, exit);
        Assert.Empty(sink.Sent);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(archive));
    }


    public static TheoryData<int?, bool> CompletionCases => new()
    {
        { null, false }, { 1, false }, { 2, false },
        { null, true }, { 1, true }, { 2, true },
    };

    [Theory]
    [MemberData(nameof(CompletionCases))]
    public async Task NormalRun_NewArchive_EmitsForExecutedAndRefusedCompletion(int? parallel, bool refused)
    {
        using var temp = new OwnedPaths();
        var suite = await WriteSuiteAsync(temp, refused);
        var archive = Path.Combine(temp.Root, "events.jsonl");
        await File.WriteAllTextAsync(archive, string.Join('\n', OldStream()));
        var sink = new RecordingSink();
        var driver = new AuthoringRefusalExitTests.TestDriver();
        var registry = new EnvironmentDriverRegistry(new IEnvironmentDriver[] { driver });

        var exit = await RunAsync(suite, archive, Hook(temp, sink), parallel, registry);

        Assert.Equal(refused ? 4 : 0, exit);
        var telemetry = Assert.Single(sink.Sent);
        Assert.Equal(1, telemetry.ScenarioCount);
        Assert.Equal(refused ? 1 : 0, telemetry.ScenarioVerdicts.Inconclusive);
        Assert.Equal(refused ? 0 : 1, telemetry.StepVerdicts.Pass);
        Assert.DoesNotContain("\"runId\":\"old\"", await File.ReadAllTextAsync(archive), StringComparison.Ordinal);
        Assert.Equal(refused ? 0 : 1, driver.StartCount);
        Assert.All(driver.Leases, lease => Assert.Equal(1, lease.DisposeCount));
        Assert.False(File.Exists(temp.ConsentStorePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NormalOrParallelRun_FailedCurrentWrite_DoesNotEmitReadableOlderArchive(int? parallel)
    {
        using var temp = new OwnedPaths();
        var suite = await WriteSuiteAsync(temp, refused: false);
        var archive = Path.Combine(temp.Root, "events.jsonl");
        var sentinel = string.Join('\n', OldStream());
        await File.WriteAllTextAsync(archive, sentinel);
        var sink = new RecordingSink();
        var driver = new AuthoringRefusalExitTests.TestDriver();
        using var held = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);

        var exit = await RunAsync(suite, archive, Hook(temp, sink), parallel,
            new EnvironmentDriverRegistry(new IEnvironmentDriver[] { driver }));

        Assert.Equal(0, exit);
        Assert.Empty(sink.Sent);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(archive));
        Assert.Equal(1, driver.StartCount);
        Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
    }

    [Fact]
    public async Task Hook_MissingCurrentReceipt_SuppressesExistingFile_AndDeletesOwnedTemp()
    {
        using var temp = new OwnedPaths();
        var archive = Path.Combine(temp.Root, "events.jsonl");
        await File.WriteAllLinesAsync(archive, OldStream());
        var sink = new RecordingSink();

        await Hook(temp, sink).EmitAsync(archive, isTempFile: true,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(sink.Sent);
        Assert.False(File.Exists(archive));
        Assert.False(File.Exists(temp.ConsentStorePath));
    }

    [Fact]
    public async Task Hook_CancelledRead_StillDeletesOwnedTemp_AndEmitsNoEvent()
    {
        using var temp = new OwnedPaths();
        var archive = Path.Combine(temp.Root, "events.jsonl");
        await File.WriteAllLinesAsync(archive, OldStream());
        var sink = new RecordingSink();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Hook(temp, sink).EmitAsync(archive, isTempFile: true,
            DateTimeOffset.UtcNow, cancellation.Token, currentArchiveWritten: true);

        Assert.Empty(sink.Sent);
        Assert.False(File.Exists(archive));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task NormalRun_CallerCancellation_StillPropagates_AndEmitsNoEvent(int? parallel)
    {
        using var temp = new OwnedPaths();
        var suite = await WriteSuiteAsync(temp, refused: false);
        var archive = Path.Combine(temp.Root, "events.jsonl");
        var sink = new RecordingSink();
        using var cancellation = new CancellationTokenSource();
        var driver = new AuthoringRefusalExitTests.TestDriver { BeforeStart = cancellation.Cancel };

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunCommand.ExecuteAsync(
                suite, SelectionCriteria.None, parallel, watch: false,
                failOnEnvironmentError: false, failOnInconclusive: false,
                htmlReportPath: null, junitReportPath: null, eventsReportPath: archive,
                eventsStreamPath: null, decorate: false, output: TextWriter.Null,
                telemetryHook: Hook(temp, sink), cancellationToken: cancellation.Token,
                environmentDrivers: new EnvironmentDriverRegistry(new IEnvironmentDriver[] { driver })));

        Assert.Equal(cancellation.Token, ex.CancellationToken);
        Assert.Empty(sink.Sent);
        Assert.False(File.Exists(temp.ConsentStorePath));
    }

    private static async Task<string> WriteSuiteAsync(OwnedPaths temp, bool refused)
    {
        var suite = Path.Combine(temp.Root, "current.e2e.yaml");
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "environment.yaml"), "owned test configuration");
        await File.WriteAllTextAsync(suite, refused ? RefusedYaml : """
            environment:
              source: { driver: test-owned, config: ./environment.yaml }
            steps:
              - id: current-pass
                type: script.csharp
                code: 'return;'
            """);
        return suite;
    }

    private static Task<int> RunAsync(string suite, string archive, TelemetryRunHook hook,
        int? parallel = null, EnvironmentDriverRegistry? registry = null) =>
        RunCommand.ExecuteAsync(
            suite, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false,
            htmlReportPath: null, junitReportPath: null, eventsReportPath: archive,
            eventsStreamPath: null, decorate: false, output: TextWriter.Null,
            telemetryHook: hook, cancellationToken: CancellationToken.None, environmentDrivers: registry);

    private static TelemetryRunHook Hook(OwnedPaths paths, RecordingSink sink) =>
        new(new TelemetryConsentStore(paths), paths, sink, noTelemetryFlag: false,
            diagnostics: TextWriter.Null,
            environmentInstallIdRaw: "87a236e0-5245-4cac-bdd2-8d5a81f80cb3",
            transportConfigured: true);

    private static string[] OldStream() => new[]
    {
        EventStreamJson.ToLine(new ScenarioStartedEvent { RunId = "old", ScenarioId = "old" }),
        EventStreamJson.ToLine(new ScenarioCompletedEvent
        {
            RunId = "old", ScenarioId = "old", Verdict = Verdict.Pass, Counts = new VerdictCounts(),
        }),
    };

    private sealed class RecordingSink : ITelemetrySink
    {
        public List<TelemetryEvent> Sent { get; } = new();
        public Task SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            Sent.Add(telemetryEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class OwnedPaths : ITelemetryPaths, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vouchfx-recovery-" + Guid.NewGuid().ToString("n"));
        public string ConsentStorePath => Path.Combine(Root, "consent.json");
        public string OutboxPath => Path.Combine(Root, "outbox.jsonl");
        public string DrainStatePath => Path.Combine(Root, "drain-state.json");
        public OwnedPaths() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
