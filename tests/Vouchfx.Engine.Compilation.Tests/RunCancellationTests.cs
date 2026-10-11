using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class RunCancellationTests
{
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, false, 60000L)]
    [InlineData(true, false, 60000L)]
    [InlineData(false, true, 60000L)]
    [InlineData(true, true, 60000L)]
    public async Task ActiveStep_CallerCancellation_JoinsBodyWithoutStartingNextStep(
        bool reusable, bool retry, long? timeoutMs)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new Dictionary<string, object> { ["started"] = started, ["release"] = release };
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars, services);
        var block = """
            {
                ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["started"]).TrySetResult(true);
                var releaseTask = ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["release"]).Task;
                await releaseTask.WaitAsync(__stepCt_hold);
                Vars["__outcome::hold"] = new Vouchfx.Engine.Abstractions.StepOutcome(Vouchfx.Engine.Abstractions.Verdict.Pass, 0L, null);
            }
            """;
        var assembled = CsxAssembler.Assemble(new[]
        {
            new StepCompilePlan("hold", new CsxFragment(Array.Empty<string>(), Array.Empty<string>(), block), retry, timeoutMs, null),
            new StepCompilePlan("next", new CsxFragment(Array.Empty<string>(), Array.Empty<string>(), "{ Vars[\"next-started\"] = true; }"), false, null, null),
        });
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);
        using var loaded = reusable ? RoslynScriptCompiler.Load(compiled) : null;
        using var cancellation = new CancellationTokenSource();
        var execution = reusable
            ? loaded!.InvokeAsync(globals, cancellation.Token)
            : RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, cancellationToken: cancellation.Token);
        var observedCancellation = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try
            {
                await execution.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
                observedCancellation = true;
            }
            catch (TimeoutException)
            {
                // A failed control must still release and join the original body below.
            }
        }
        finally
        {
            release.TrySetResult(true);
            try { await execution; }
            catch (OperationCanceledException) { }
        }

        Assert.True(observedCancellation, "The active step did not observe caller cancellation.");
        Assert.False(vars.ContainsKey("next-started"));
        Assert.False(vars.ContainsKey(VarKeys.Outcome("hold")));
        Assert.Equal(2, services.Count);
        Assert.Same(started, services["started"]);
        Assert.Same(release, services["release"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_CancellationAfterFirstAttempt_InterruptsBackoff(bool reusable)
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellingSink(cancellation);
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars, new Dictionary<string, object>(),
            Vouchfx.Engine.Abstractions.Secrets.NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            Vouchfx.Engine.Abstractions.Traces.NullTraceCaptureAccessor.Instance, sink);
        var fragment = new CsxFragment(Array.Empty<string>(), Array.Empty<string>(), """
            {
                await System.Threading.Tasks.Task.CompletedTask;
                Vars["__outcome::hold"] = new Vouchfx.Engine.Abstractions.StepOutcome(Vouchfx.Engine.Abstractions.Verdict.Fail, 0L, null);
            }
            """);
        var compiled = RoslynScriptCompiler.CompileOnce(CsxAssembler.Assemble(new[]
        {
            new StepCompilePlan("hold", fragment, true, 60000L, 60000L),
        }).CsxSource);
        using var loaded = reusable ? RoslynScriptCompiler.Load(compiled) : null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reusable
            ? loaded!.InvokeAsync(globals, cancellation.Token)
            : RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, cancellationToken: cancellation.Token));
        Assert.Equal(1, sink.Attempts);
        Assert.Equal(0, sink.Completed);
        Assert.False(vars.ContainsKey(VarKeys.Outcome("hold")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservedInvocationService_CannotReplaceEngineCancellation(bool reusable)
    {
        var vars = new Dictionary<string, object?>();
        var services = new Dictionary<string, object> { ["__vouchfx_run_cancellation"] = CancellationToken.None };
        var globals = new ScriptGlobalVariables(vars, services);
        var compiled = RoslynScriptCompiler.CompileOnce("Vars[\"started\"] = true;");
        using var loaded = reusable ? RoslynScriptCompiler.Load(compiled) : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => reusable
            ? loaded!.InvokeAsync(globals)
            : RoslynScriptCompiler.RunIsolatedAsync(compiled, globals));
        Assert.Empty(vars);
        Assert.Single(services);
    }

    [Fact]
    public async Task LoadedScript_ConcurrentInvocations_HaveIndependentCancellationAndOriginalClients()
    {
        var client = new object();
        var started = new[]
        {
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = started.Select(signal => new Dictionary<string, object>
        {
            ["started"] = signal,
            ["release"] = release,
            ["client"] = client,
        }).ToArray();
        var vars = new[] { new Dictionary<string, object?>(), new Dictionary<string, object?>() };
        var fragment = new CsxFragment(Array.Empty<string>(), Array.Empty<string>(), """
            {
                Vars["client"] = Services["client"];
                ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["started"]).TrySetResult(true);
                var releaseTask = ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["release"]).Task;
                await releaseTask.WaitAsync(__stepCt_hold);
                Vars["done"] = true;
            }
            """);
        var compiled = RoslynScriptCompiler.CompileOnce(CsxAssembler.Assemble(new[]
        {
            new StepCompilePlan("hold", fragment, false, null, null),
        }).CsxSource);
        using var loaded = RoslynScriptCompiler.Load(compiled);
        using var cancellation = new CancellationTokenSource();
        var first = loaded.InvokeAsync(new ScriptGlobalVariables(vars[0], services[0]), cancellation.Token);
        var second = loaded.InvokeAsync(new ScriptGlobalVariables(vars[1], services[1]));
        try
        {
            await Task.WhenAll(started.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            try { await first; }
            catch (OperationCanceledException) { }
            await second;
        }
        Assert.False(vars[0].ContainsKey("done"));
        Assert.Equal(true, vars[1]["done"]);
        Assert.All(vars, map => Assert.Same(client, map["client"]));
        Assert.All(services, map => Assert.Equal(3, map.Count));
    }

    private sealed class CancellingSink : IStepEventSink
    {
        private readonly CancellationTokenSource _cancellation;
        internal int Attempts { get; private set; }
        internal int Completed { get; private set; }
        internal CancellingSink(CancellationTokenSource cancellation) => _cancellation = cancellation;
        public void OnStepStarted(string stepId) { }
        public void OnStepAttempt(string stepId, Vouchfx.Engine.Abstractions.Retry.AttemptRecord attempt)
        {
            Attempts++;
            _cancellation.Cancel();
        }
        public void OnStepCompleted(string stepId, StepOutcome? outcome, string? captureStatus) => Completed++;
    }
}
