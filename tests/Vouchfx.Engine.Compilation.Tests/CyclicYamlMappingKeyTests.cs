using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class CyclicYamlMappingKeyTests
{
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(HttpSoapProvider).Assembly,
        typeof(MetricsAssertPrometheusProvider).Assembly,
    });

    [Fact]
    public void DocumentValidator_SelfContainingSequenceKey_ReturnsWithoutOverflowing()
    {
        const string yaml = """
            steps:
              - id: call
                type: http.rest
                method: POST
                path: /x
                body: { ? &k [ *k ] : v }
            """;

        var result = DocumentValidator.Validate(yaml, s_registry);
        Assert.NotNull(result);
    }
}
