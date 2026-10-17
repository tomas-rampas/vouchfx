using System.IO.Pipes;
using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>Real EOF, compiler and command pipeline; the owned topology lease needs no Docker.</summary>
public sealed class ActiveStdinEofExitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vouchfx-active-eof-").FullName;

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ActiveRun_AbsorbedEofCancellation_ExitsFourAfterDisposal(int? parallel)
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        WriteScenario("hold", Hold);
        var execution = fixture.RunAsync(_root, parallel);
        try
        {
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await fixture.CloseInputAsync();
            Assert.Equal(ExitCodes.Inconclusive, await execution.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, fixture.Disposals);
            if (parallel is null)
                Assert.Contains(RunCommand.StdinEofShutdownNotice, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal("INCONCLUSIVE", Assert.Single(Completed()).GetProperty("verdict").GetString());
        }
        finally { await fixture.JoinAsync(execution); }
    }

    [Fact]
    public async Task EofBeforeAcquisitionCompletes_StillExitsFour()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        fixture.HoldAcquisition = true;
        WriteScenario("hold", Hold);
        var execution = fixture.RunAsync(_root);
        try
        {
            await fixture.Acquiring.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await fixture.CloseInputAsync();
            Assert.Equal(ExitCodes.Inconclusive, await execution.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(0, fixture.Disposals);
            Assert.Contains(RunCommand.StdinEofShutdownNotice, fixture.Output.ToString(), StringComparison.Ordinal);
        }
        finally { await fixture.JoinAsync(execution); }
    }

    [Fact]
    public async Task CompletedPass_EofDuringDisposal_PreservesSuccess()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        fixture.CloseDuringDisposal = true;
        WriteScenario("pass", "return;");
        var execution = fixture.RunAsync(_root);
        try
        {
            Assert.Equal(ExitCodes.Success, await execution.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, fixture.Disposals);
            Assert.Equal("PASS", Assert.Single(Completed()).GetProperty("verdict").GetString());
            Assert.DoesNotContain(RunCommand.StdinEofShutdownNotice, fixture.Output.ToString(), StringComparison.Ordinal);
        }
        finally { await fixture.JoinAsync(execution); }
    }

    [Theory]
    [InlineData(false, ExitCodes.Success)]
    [InlineData(true, ExitCodes.Inconclusive)]
    public async Task OrdinaryExecutedInconclusive_RemainsOptIn(bool strict, int expected)
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        WriteScenario("hold", Hold, "100ms");
        var execution = fixture.RunAsync(_root, strict: strict);
        try
        {
            Assert.Equal(expected, await execution.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(fixture.Started.Task.IsCompletedSuccessfully);
            Assert.Equal("INCONCLUSIVE", Assert.Single(Completed()).GetProperty("verdict").GetString());
            Assert.DoesNotContain(RunCommand.StdinEofShutdownNotice, fixture.Output.ToString(), StringComparison.Ordinal);
        }
        finally { await fixture.JoinAsync(execution); }
    }

    [Fact]
    public async Task EarlierFailure_BesideEofCancelledScenario_PreservesFailureExit()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        WriteScenario("a-fail", "throw new System.InvalidOperationException(\"expected failure\");");
        WriteScenario("b-hold", Hold);
        var execution = fixture.RunAsync(_root);
        try
        {
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await fixture.CloseInputAsync();
            Assert.Equal(ExitCodes.TestFailure, await execution.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains(Completed(), item => item.GetProperty("verdict").GetString() == "FAIL");
            Assert.Contains(Completed(), item => item.GetProperty("verdict").GetString() == "INCONCLUSIVE");
        }
        finally { await fixture.JoinAsync(execution); }
    }

    private const string Hold = """
        ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["started"]).TrySetResult(true);
        await ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["release"]).Task.WaitAsync(__stepCt_hold);
        """;

    private void WriteScenario(string name, string code, string timeout = "60s") =>
        File.WriteAllText(Path.Combine(_root, name + ".e2e.yaml"), $$"""
            metadata:
              name: {{name}}
            steps:
              - id: hold
                type: script.csharp
                timeout: {{timeout}}
                code: |
                  {{code.Replace("\n", "\n      ", StringComparison.Ordinal)}}
            """);

    private JsonElement[] Completed() => File.ReadLines(Path.Combine(_root, "events.jsonl"))
        .Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); })
        .Where(item => item.GetProperty("type").GetString() == "scenario-completed").ToArray();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly NamedPipeServerStream _input;
        private readonly NamedPipeClientStream _writer;
        private readonly CancellationTokenSource _caller = new();
        private CancellationToken _runToken;
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Acquiring = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StringWriter Output { get; } = new();
        internal bool HoldAcquisition;
        internal bool CloseDuringDisposal;
        internal int Disposals;

        private Fixture(string root, NamedPipeServerStream input, NamedPipeClientStream writer)
            => (_root, _input, _writer) = (root, input, writer);

        internal static async Task<Fixture> CreateAsync(string root)
        {
            var name = "vouchfx-eof-" + Guid.NewGuid().ToString("N");
            var input = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var writer = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
            try
            {
                await Task.WhenAll(input.WaitForConnectionAsync(), writer.ConnectAsync(5000))
                    .WaitAsync(TimeSpan.FromSeconds(10));
                return new Fixture(root, input, writer);
            }
            catch { await writer.DisposeAsync(); await input.DisposeAsync(); throw; }
        }

        internal Task<int> RunAsync(string path, int? parallel = null, bool strict = false)
        {
            var drivers = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), async (_, ct) =>
            {
                _runToken = ct;
                Acquiring.TrySetResult(true);
                if (HoldAcquisition) await Task.Delay(Timeout.Infinite, ct);
                return new Lease(this);
            });
            return RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel, false, false, strict,
                null, null, Path.Combine(_root, "events.jsonl"), null, false, Output, null, _caller.Token,
                shutdownOnStdinEof: true, environmentDrivers: drivers, shutdownInput: _input);
        }

        internal ValueTask CloseInputAsync() => _writer.DisposeAsync();

        internal async Task JoinAsync(Task<int> execution)
        {
            _release.TrySetResult(true);
            _caller.Cancel();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            await _input.DisposeAsync();
            _caller.Dispose();
        }

        private sealed class Lease : IKeptTopology
        {
            private readonly Fixture _owner;
            internal Lease(Fixture owner) => _owner = owner;
            public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
            public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
            public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
            public IReadOnlyDictionary<string, object> DiscoveredServices => new Dictionary<string, object>
            { ["started"] = _owner.Started, ["release"] = _owner._release };
            public IReadOnlyList<string> DependencyNames => Array.Empty<string>();
            public IReadOnlyDictionary<string, string> DependencyTypes => new Dictionary<string, string>();
            public Task ReseedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public async ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _owner.Disposals);
                if (!_owner.CloseDuringDisposal) return;
                await _owner.CloseInputAsync();
                try { await Task.Delay(Timeout.Infinite, _owner._runToken).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (_owner._runToken.IsCancellationRequested) { }
            }
        }
    }
}
