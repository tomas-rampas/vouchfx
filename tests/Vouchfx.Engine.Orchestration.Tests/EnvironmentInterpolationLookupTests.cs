using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class EnvironmentInterpolationLookupTests
{
    [Theory]
    [InlineData("Service", true, "")]
    [InlineData("Dependency", false, "")]
    [InlineData("Service", true, "value")]
    [InlineData("Dependency", false, "value")]
    [InlineData("Service", true, null)]
    [InlineData("Dependency", false, null)]
    public void ValidationAndExpressionBuild_DistinguishEmptyFromUnset(
        string ownerLabel, bool connectionReferencesSupported, string? processValue)
    {
        const string variable = "VOUCHFX_EMPTY_LOOKUP_PROBE";
        const string template = "prefix-${env:VOUCHFX_EMPTY_LOOKUP_PROBE}-suffix";
        var lookups = new List<string>();
        Func<string, string?> lookup = name =>
        {
            lookups.Add(name);
            return processValue;
        };
        var validate = Helper("ValidateEnvValue");
        var build = Helper("BuildEnvExpression");
        var accessType = build.GetParameters()[4].ParameterType.GetGenericArguments()[1];
        var accessors = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), accessType))!;
        object?[] validationArguments =
        {
            ownerLabel, "target", "SETTING", template,
            new Dictionary<string, DependencySpec>(), connectionReferencesSupported, lookup,
        };
        object?[] buildArguments = { ownerLabel, "target", "SETTING", template, accessors, lookup };

        if (processValue is null)
        {
            // Invoke each helper independently: the defensive build refusal must survive even
            // when eager validation is bypassed, and vice versa.
            foreach (var (method, arguments) in new[] { (validate, validationArguments), (build, buildArguments) })
            {
                var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, arguments));
                var cause = Assert.IsType<ArgumentException>(error.InnerException);
                Assert.Contains($"{ownerLabel} 'target' env entry 'SETTING'", cause.Message, StringComparison.Ordinal);
                Assert.Contains(variable, cause.Message, StringComparison.Ordinal);
                Assert.Contains("no environment variable", cause.Message, StringComparison.Ordinal);
            }
        }
        else
        {
            validate.Invoke(null, validationArguments);
            var expression = Assert.IsType<ReferenceExpression>(build.Invoke(null, buildArguments));
            Assert.Equal($"prefix-{processValue}-suffix", expression.ValueExpression);
        }

        Assert.Equal(new[] { variable, variable }, lookups);
    }

    private static MethodInfo Helper(string name) =>
        typeof(EnvironmentMapper).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
}
