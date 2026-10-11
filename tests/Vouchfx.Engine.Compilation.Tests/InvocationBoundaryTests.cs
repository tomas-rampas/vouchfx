using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Abstractions.Webhooks;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class InvocationBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Invocation_PreservesCaseInsensitiveServiceLookup(bool reusable, bool cancellable)
    {
        var client = new object();
        var services = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["client"] = client };
        var vars = new Dictionary<string, object?>();
        using var cancellation = new CancellationTokenSource();
        var compiled = RoslynScriptCompiler.CompileOnce("Vars[\"client\"] = Services[\"CLIENT\"]; Vars[\"found\"] = Services.ContainsKey(\"CLIENT\") && Services.TryGetValue(\"CLIENT\", out var value) && object.ReferenceEquals(value, Services[\"CLIENT\"]);");
        using var loaded = reusable ? RoslynScriptCompiler.Load(compiled) : null;
        await Execute(compiled, loaded, new ScriptGlobalVariables(vars, services), cancellable ? cancellation.Token : default);
        Assert.Same(client, vars["client"]);
        Assert.Equal(true, vars["found"]);
        Assert.Single(services);
        Assert.False(services.ContainsKey(ScriptInvocationContext.CancellationServiceKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellableInvocation_ObservesLiveServiceUpdates(bool reusable)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = new object();
        var after = new object();
        var services = new Dictionary<string, object> { ["client"] = before, ["started"] = started, ["release"] = release };
        var vars = new Dictionary<string, object?>();
        using var cancellation = new CancellationTokenSource();
        var compiled = RoslynScriptCompiler.CompileOnce("""
            ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["started"]).TrySetResult(true);
            var releaseTask = ((System.Threading.Tasks.TaskCompletionSource<bool>)Services["release"]).Task;
            await releaseTask;
            Vars["client"] = Services["client"];
            Vars["late"] = Services.TryGetValue("late", out var late) ? late : null;
            Vars["keys"] = false;
            foreach (var key in Services.Keys) if (key == "late") Vars["keys"] = true;
            Vars["values"] = false;
            foreach (var value in Services.Values) if (object.ReferenceEquals(value, Services["client"])) Vars["values"] = true;
            Vars["enumeration"] = false;
            foreach (var pair in Services) if (pair.Key == "late") Vars["enumeration"] = true;
            Vars["count"] = Services.Count;
            """);
        using var loaded = reusable ? RoslynScriptCompiler.Load(compiled) : null;
        var execution = Execute(compiled, loaded, new ScriptGlobalVariables(vars, services), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            services["client"] = after;
            services["late"] = after;
        }
        finally
        {
            release.TrySetResult(true);
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Same(after, vars["client"]);
        Assert.Same(after, vars["late"]);
        Assert.Equal(true, vars["keys"]);
        Assert.Equal(true, vars["values"]);
        Assert.Equal(true, vars["enumeration"]);
        Assert.Equal(services.Count + 1, vars["count"]);
        Assert.False(services.ContainsKey(ScriptInvocationContext.CancellationServiceKey));
    }

    [Fact]
    public void DefaultInvocation_PreservesOriginalGlobalsAndMapIdentity()
    {
        var services = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["client"] = new object() };
        var globals = new ScriptGlobalVariables(new Dictionary<string, object?>(), services);
        Assert.Same(globals, ScriptInvocationContext.Create(globals, default));
        Assert.Same(services, globals.Services);
        Assert.Single(services);
    }

    [Fact]
    public void InvocationToken_CannotBeOverriddenByLaterCallerMapMutation()
    {
        var services = new Dictionary<string, object>();
        var globals = new ScriptGlobalVariables(new Dictionary<string, object?>(), services);
        using var cancellation = new CancellationTokenSource();
        var wrapped = ScriptInvocationContext.Create(globals, cancellation.Token);
        Assert.Same(globals.Vars, wrapped.Vars);
        Assert.Same(globals.Secrets, wrapped.Secrets);
        Assert.Same(globals.Webhooks, wrapped.Webhooks);
        Assert.Same(globals.Traces, wrapped.Traces);
        Assert.Same(globals.StepEvents, wrapped.StepEvents);
        Assert.Same(globals.Security, wrapped.Security);
        services[ScriptInvocationContext.CancellationServiceKey] = CancellationToken.None;
        Assert.Equal(cancellation.Token, wrapped.Services[ScriptInvocationContext.CancellationServiceKey]);
        Assert.Single(wrapped.Services);
        Assert.Single(wrapped.Services.Keys);
        Assert.Single(wrapped.Services.Values);
        Assert.Single(services);
        Assert.Equal(CancellationToken.None, services[ScriptInvocationContext.CancellationServiceKey]);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 60000L)]
    [InlineData(true, 60000L)]
    public async Task ActualKafkaImmediate_CallerCancellationAtStarted_PreventsFinalOutcomeAndCompletion(bool reusable, long? timeoutMs)
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new BoundaryCancellingSink(cancellation, cancelAtAttempt: false);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var vars = new Dictionary<string, object?> { ["conn::broker"] = $"127.0.0.1:{endpoint.Port}" };
            await AssertKafkaCancellation(reusable, retry: false, timeoutMs, vars, sink, cancellation);
            Assert.Equal(1, sink.Started);
            Assert.Equal(0, sink.Attempts);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualKafkaRetry_CallerCancellationAtTerminalAttempt_PreventsFinalOutcomeAndCompletion(bool reusable)
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new BoundaryCancellingSink(cancellation, cancelAtAttempt: true);
        var vars = new Dictionary<string, object?> { ["conn::broker"] = "" };
        await AssertKafkaCancellation(reusable, retry: true, 60000L, vars, sink, cancellation);
        Assert.Equal(1, sink.Started);
        Assert.Equal(1, sink.Attempts);
        Assert.Equal(Verdict.EnvironmentError, sink.AttemptVerdict);
    }

    private static async Task AssertKafkaCancellation(bool reusable, bool retry, long? timeoutMs,
        Dictionary<string, object?> vars, BoundaryCancellingSink sink, CancellationTokenSource cancellation)
    {
        var fragment = new MqExpectKafkaProvider().Emit(
            new MqExpectKafkaModel("broker", "cancelled-retained", new KafkaMatch(null, null, "expected", null)),
            new KafkaCompileContext());
        var assembled = CsxAssembler.Assemble(new[] { new StepCompilePlan("kafka", fragment, retry, timeoutMs, null) });
        var references = Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Where(IsManagedAssembly).ToArray();
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: references);
        using var loaded = reusable ? RoslynScriptCompiler.Load(compiled) : null;
        var globals = new ScriptGlobalVariables(vars, new Dictionary<string, object>(), NullSecretAccessor.Instance,
            NullWebhookCaptureAccessor.Instance, NullTraceCaptureAccessor.Instance, sink);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Execute(compiled, loaded, globals, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, sink.Completed);
        Assert.False(vars.ContainsKey(VarKeys.Outcome("kafka")));
    }

    private static Task<object?> Execute(CompiledScript compiled, LoadedScript? loaded, ScriptGlobalVariables globals, CancellationToken ct)
        => loaded is null ? RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, cancellationToken: ct) : loaded.InvokeAsync(globals, ct);

    private static bool IsManagedAssembly(string path)
    {
        try { AssemblyName.GetAssemblyName(path); return true; }
        catch (BadImageFormatException) { return false; }
    }

    private sealed class BoundaryCancellingSink : IStepEventSink
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly bool _cancelAtAttempt;
        internal int Started { get; private set; }
        internal int Attempts { get; private set; }
        internal int Completed { get; private set; }
        internal Verdict? AttemptVerdict { get; private set; }
        internal BoundaryCancellingSink(CancellationTokenSource cancellation, bool cancelAtAttempt)
        {
            _cancellation = cancellation;
            _cancelAtAttempt = cancelAtAttempt;
        }
        public void OnStepStarted(string stepId)
        {
            Started++;
            if (!_cancelAtAttempt) _cancellation.Cancel();
        }
        public void OnStepAttempt(string stepId, AttemptRecord attempt)
        {
            Attempts++;
            AttemptVerdict = attempt.Verdict;
            if (_cancelAtAttempt) _cancellation.Cancel();
        }
        public void OnStepCompleted(string stepId, StepOutcome? outcome, string? captureStatus) => Completed++;
    }

    private sealed class KafkaCompileContext : ICompileContext
    {
        public string StepId => "kafka";
        public string SuiteNamespace => "Cancellation";
        public string SuiteDirectory => AppContext.BaseDirectory;
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
    }
}
