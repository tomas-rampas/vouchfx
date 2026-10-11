using System.Collections.Concurrent;
using System.Diagnostics;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Reporting;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class SerialScenarioSchedulingTests
{
    private static readonly string[] AllNames = { "first", "second", "third" };
    private static readonly string[] StartedBeforeCancellation = { "first", "second" };

    [Theory]
    [InlineData(Verdict.Pass, false)]
    [InlineData(Verdict.Fail, false)]
    [InlineData(Verdict.EnvironmentError, false)]
    [InlineData(Verdict.Pass, true)]
    public async Task DegreeOne_WithReverseContinuations_ExecutesInDeclarationOrderAndCompletesAll(
        Verdict firstVerdict, bool cancelSecond)
    {
        var observed = await Task.Run(() =>
        {
            using var cancellation = new CancellationTokenSource();
            using var context = new ReverseContinuationContext();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });
                var ast = AstBuilder.Build(YamlDocumentParser.Parse("""
                    steps:
                      - id: request
                        type: http.rest
                        target: api
                        method: GET
                        path: /
                        expect: { status: 200 }
                    """), registry);
                var names = AllNames;
                var yamls = new string[names.Length];
                Array.Fill(yamls, "steps: []");
                var entries = new List<string>();
                var cleanup = new List<string>();
                var output = new StringWriter();
                ParallelSuiteRunner.ScenarioCoreFunc core = (_, _, name, _, _, _, _, _, ct) =>
                {
                    entries.Add(name);
                    try
                    {
                        if (cancelSecond && name == "second")
                        {
                            cancellation.Cancel();
                            ct.ThrowIfCancellationRequested();
                        }

                        var verdict = name == "first" ? firstVerdict : Verdict.Pass;
                        return Task.FromResult(new ScenarioCoreResult(verdict, Buffer(name, verdict)));
                    }
                    finally
                    {
                        cleanup.Add(name);
                    }
                };

                var run = ParallelSuiteRunner.RunParallelCoreAsync(
                    registry, new[] { ast, ast, ast }, names, yamls,
                    appHostAssemblyName: null, output, diffLookup: (_, _) => null, maxConcurrency: 1,
                    runScenario: core, seedBaseDirectory: null, ct: cancellation.Token);
                context.PumpUntilComplete(run);
                return (Result: run.GetAwaiter().GetResult(), Entries: entries.ToArray(),
                    Cleanup: cleanup.ToArray(), CallerCancelled: cancellation.IsCancellationRequested,
                    Output: output.ToString());
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });

        // Rendering is already ordered; assert real core entry and completion instead.
        var expectedEntries = cancelSecond ? StartedBeforeCancellation : AllNames;
        Assert.Equal(expectedEntries, observed.Entries);
        Assert.Equal(expectedEntries, observed.Cleanup);
        Assert.Equal(cancelSecond, observed.CallerCancelled);
        Assert.Equal(AllNames, observed.Result.ScenarioVerdicts.Select(x => x.ScenarioName));
        Assert.Equal(firstVerdict, observed.Result.ScenarioVerdicts[0].Verdict);
        Assert.Equal(cancelSecond ? Verdict.Inconclusive : Verdict.Pass, observed.Result.ScenarioVerdicts[1].Verdict);
        Assert.Equal(cancelSecond ? Verdict.Inconclusive : Verdict.Pass, observed.Result.ScenarioVerdicts[2].Verdict);
        if (cancelSecond)
        {
            Assert.Contains("PASS", observed.Output, StringComparison.Ordinal);
        }
    }

    private static List<string> Buffer(string name, Verdict verdict)
    {
        return new List<string>
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = "run-" + name,
                Timestamp = DateTimeOffset.UnixEpoch,
                ScenarioId = name,
            }),
            EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = "run-" + name,
                Timestamp = DateTimeOffset.UnixEpoch,
                ScenarioId = name,
                Verdict = verdict,
                Counts = new VerdictCounts { Pass = verdict == Verdict.Pass ? 1 : 0 },
            }),
        };
    }

    private sealed class ReverseContinuationContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentStack<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly AutoResetEvent _work = new(false);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Push((callback, state));
            _work.Set();
        }

        internal void PumpUntilComplete(Task task)
        {
            var wakeOnCompletion = task.ContinueWith(_ => _work.Set(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var started = Stopwatch.GetTimestamp();
            var timeout = TimeSpan.FromSeconds(10);
            while (!task.IsCompleted)
            {
                if (_pending.TryPop(out var next))
                {
                    next.Callback(next.State);
                    continue;
                }

                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero || (!_work.WaitOne(remaining) && !task.IsCompleted))
                {
                    throw new TimeoutException("Serial scheduling did not complete its queued continuations.");
                }
            }

            // Join this notification before disposing its wait handle.
            wakeOnCompletion.GetAwaiter().GetResult();
        }

        public void Dispose() => _work.Dispose();
    }
}
