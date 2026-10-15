using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class HttpTlsProviderCompilationTests
{
    [Fact]
    public void WholeHttpFamily_CompilesWithOneNewHelperAndFrozenOldHelper()
    {
        var rest = new HttpRestProvider();
        var soap = new HttpSoapProvider();
        var metrics = new MetricsAssertPrometheusProvider();
        Assert.Equal("1.0.2", rest.Metadata.MinEngineVersion);
        Assert.Equal("1.0.2", soap.Metadata.MinEngineVersion);
        Assert.Equal("1.0.2", metrics.Metadata.MinEngineVersion);
        var fragments = new[]
        {
            ("rest", rest.Emit(new HttpRestModel("sut", "GET", "/", null, null, null), new Context("rest"))),
            ("soap", soap.Emit(new HttpSoapModel("sut", "/", null, "<a/>", null), new Context("soap"))),
            ("metrics", metrics.Emit(new MetricsAssertPrometheusModel("sut", "/metrics", "total", null, new MetricsExpectation("1", null, null)), new Context("metrics"))),
        };
        foreach (var (_, fragment) in fragments)
        {
            Assert.Contains(HttpTlsHelper.Source, fragment.RequiredHelpers);
            Assert.DoesNotContain(SecurityHelper.Source, fragment.RequiredHelpers);
            Assert.Contains(fragment.RequiredHelpers, source => source.Contains("new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false }", StringComparison.Ordinal));
        }
        var legacy = new CsxFragment(RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: new[] { SecurityHelper.Source }, StatementBlock: "{ }");
        var assembled = CsxAssembler.Assemble(fragments.Append(("legacy", legacy)).ToArray()).CsxSource;
        Assert.Equal(1, assembled.Split("static class HttpTls_Helpers", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, assembled.Split("HttpTls_Helpers.ConfigureHandler(security, targetName, handler)", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("using var", assembled, StringComparison.Ordinal);
        // Use provider-contributed references and ordinary HTTP support types.
        // The compiler must supply the shared TLS helper's framework references;
        // a full-TPA test-only reference list would conceal a missing default.
        var references = new ICompileReferenceContributor[] { rest, soap, metrics }
            .SelectMany(provider => provider.CompileReferenceAssemblies)
            .Concat(new[]
            {
                typeof(System.Net.HttpStatusCode).Assembly,
                typeof(System.Text.Json.JsonSerializer).Assembly,
                typeof(Uri).Assembly,
            })
            .Select(assembly => assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // Compile only: none of the emitted HTTP operations execute.
        Assert.NotNull(RoslynScriptCompiler.CompileOnce(assembled, additionalReferencePaths: references));
    }

    private sealed class Context : ICompileContext
    {
        public Context(string stepId) => StepId = stepId;
        public string SuiteDirectory => Directory.GetCurrentDirectory();
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
    }
}
