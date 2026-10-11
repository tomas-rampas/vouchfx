using System.Reflection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class PreTopologyFaultRegressionTests
{
    private static readonly Assembly[] s_providers = { typeof(ScriptCsharpProvider).Assembly };
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(s_providers);
    private static readonly string[] s_faultNames = { "fault" };
    private static readonly string[] s_invalidNames = { "invalid" };
    private static readonly string[] s_yamlTexts = { Yaml };
    private const string Canary = "private-driver-message-canary";
    private const string Yaml = """
        environment:
          source: { driver: diagnostic-fault, config: environment.fixture }
        steps:
          - id: local
            type: script.csharp
            code: 'return;'
        """;

    [Theory]
    [InlineData("ordinary", nameof(InvalidOperationException))]
    [InlineData("cancellation", nameof(OperationCanceledException))]
    [InlineData("hostile-message", nameof(HostileMessageException))]
    public void Validate_UnexpectedBindEscape_IsAPhaseOwnedSafeDiagnostic(string kind, string type)
    {
        var driver = new FaultDriver(() => Fault(kind));
        var result = ScenarioValidator.ValidateScenario(Yaml, "fault.e2e.yaml", s_registry,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
        Assert.Contains("pre-topology", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(type, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, driver.BindCalls);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("cancellation")]
    [InlineData("hostile-message")]
    public async Task Serial_UnexpectedBindEscape_CarriesFaultAndRefusalWithoutAcquisition(string kind)
    {
        var driver = new FaultDriver(() => Fault(kind));
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_registry);
        var output = new StringWriter();
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast }, s_faultNames,
            s_yamlTexts, s_providers, "unused-host", output,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(result.ProviderOrEngineFaultObserved);
        Assert.True(result.AuthoringRefusalObserved);
        Assert.False(result.ExecutedAnyScenario);
        Assert.Contains("pre-topology", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Canary, output.ToString(), StringComparison.Ordinal);
        Assert.True(driver.BindCalls > 0);
    }

    [Fact]
    public async Task CallerCancellationDuringBind_PropagatesTheActualTokenWithoutAFaultVerdict()
    {
        using var stop = new CancellationTokenSource();
        var driver = new FaultDriver(() =>
        {
            stop.Cancel();
            return new OperationCanceledException(stop.Token);
        });
        var output = new StringWriter();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScenarioRunner.RunAsync(
            Yaml, "cancelled", s_providers, "unused-host", output,
            cancellationToken: stop.Token, environmentDrivers: new EnvironmentDriverRegistry(new[] { driver })));
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.Equal(1, driver.BindCalls);
        Assert.DoesNotContain("pre-topology", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("step-completed", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclaredAuthoringBindingFailure_DoesNotAcquireEngineFaultProvenance()
    {
        var driver = new FaultDriver(() => new ArgumentException("own authoring error"));
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_registry);
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast }, s_invalidNames,
            s_yamlTexts, s_providers, "unused-host", new StringWriter(),
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(result.AuthoringRefusalObserved);
        Assert.False(result.ProviderOrEngineFaultObserved);
        Assert.False(result.ExecutedAnyScenario);
    }

    private static Exception Fault(string kind) => kind switch
    {
        "ordinary" => new InvalidOperationException(Canary),
        "cancellation" => new OperationCanceledException(Canary),
        "hostile-message" => new HostileMessageException(),
        _ => throw new ArgumentException("unknown test fault", nameof(kind)),
    };

    private sealed class HostileMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException(Canary);
    }

    private sealed class FaultDriver : IEnvironmentDriver
    {
        private readonly Func<Exception> _fault;
        internal FaultDriver(Func<Exception> fault) => _fault = fault;
        internal int BindCalls { get; private set; }
        public string Id => "diagnostic-fault";
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            BindCalls++;
            throw _fault();
        }
    }
}
