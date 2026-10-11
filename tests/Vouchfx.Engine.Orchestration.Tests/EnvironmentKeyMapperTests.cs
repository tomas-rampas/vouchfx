using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class EnvironmentKeyMapperTests : IDisposable
{
    private readonly ProjectFixtures _projects = new();
    public void Dispose() => _projects.Dispose();
    public static TheoryData<string, string> ReservedSuffixes => new()
    {
        { "elasticsearch", "discovery.type" }, { "elasticsearch", "xpack.security.enabled" },
        { "elasticsearch", "ES_JAVA_OPTS" }, { "elasticsearch", "cluster.routing.allocation.disk.threshold_enabled" },
        { "minio", "MINIO_ROOT_USER" }, { "minio", "MINIO_ROOT_PASSWORD" },
        { "s3", "RUSTFS_ACCESS_KEY" }, { "s3", "RUSTFS_SECRET_KEY" },
        { "azureservicebus", "ACCEPT_EULA" }, { "azureservicebus", "MSSQL_SA_PASSWORD" }, { "azureservicebus", "SQL_SERVER" },
        { "postgres", "UNRESERVED" }, { "image", "UNRESERVED" }, { "project", "UNRESERVED" },
    };

    [Theory]
    [MemberData(nameof(ReservedSuffixes))]
    public void TypedMap_RefusesMalformedNameBeforeReturningConfigure(string kind, string prefix)
    {
        var error = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(Environment(kind, prefix + "=credential_canary_607", "value_canary_607")));
        Assert.Contains("Environment variable names", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_canary_607", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("value_canary_607", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("image", "${secret:env/NAME}", "secret:")]
    [InlineData("postgres", "${secret:env/NAME}", "secret:")]
    [InlineData("image", "${conn:unknown}", "unknown dependency")]
    [InlineData("postgres", "${conn:unknown}", "connection SOURCE")]
    [InlineData("image", "${env:}", "not well-formed")]
    public void TypedMap_BadNamePreservesValueFirstReasonWithoutNameLeak(string kind, string value, string reason)
    {
        var error = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(Environment(kind, "BAD=credential_canary_607", value)));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_canary_607", error.Message, StringComparison.Ordinal);
        Assert.Equal("envValue", error.ParamName);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("FOO\0", "")]
    [InlineData("FOO\n", "")]
    [InlineData("FOO\r\n", "")]
    [InlineData("BAD/name", "")]
    [InlineData("BAD~name", "")]
    public void TypedMap_InvalidControls_AreRefused(string key, string value)
        => Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(Environment("postgres", key, value)));

    [Fact]
    public void DefensiveBuildExpression_MalformedName_DoesNotLeakInValueRefusal()
    {
        var method = typeof(EnvironmentMapper).GetMethod("BuildEnvExpression", BindingFlags.NonPublic | BindingFlags.Static)!;
        var accessorType = method.GetParameters()[4].ParameterType.GetGenericArguments()[1];
        var accessors = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), accessorType))!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[]
        {
            "Service", "app", "BAD=credential_canary_607", "${env:VOUCHFX_ENV607_DEFINITELY_ABSENT}",
            accessors,
        }));
        var cause = Assert.IsType<ArgumentException>(error.InnerException);
        Assert.DoesNotContain("credential_canary_607", cause.Message, StringComparison.Ordinal);
        Assert.Contains("environment variable", cause.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("project")]
    [InlineData("postgres")]
    public async Task Configure_PreservesDottedCaseSensitiveNamesAndValues(string kind)
    {
        var mapped = EnvironmentMapper.Map(Environment(kind, "VOUCHFX_PROBE.key_1", "ExactCaseValue"));
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = "Vouchfx.Engine.Orchestration.Tests",
        });
        mapped.Configure(builder);
        var resource = builder.Resources.Single(r => r.Name == (kind is "image" or "project" ? "app" : "store"));
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, values);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) await callback.Callback(context);
        var expression = Assert.IsType<ReferenceExpression>(values["VOUCHFX_PROBE.key_1"]);
        Assert.Equal("ExactCaseValue", expression.ValueExpression);
        Assert.DoesNotContain("vouchfx_probe.key_1", values.Keys);
    }

    private EnvironmentSpec Environment(string kind, string key, string value)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
        var project = kind == "project" ? _projects.CreateWithHttpEndpoint() : null;
        return new EnvironmentSpec(kind is "image" or "project" ? new Dictionary<string, ServiceSpec> { ["app"] = new(kind == "image" ? "app:1.0" : null, project, null, null, values) } : null,
            kind is not ("image" or "project") ? new Dictionary<string, DependencySpec> { ["store"] = new(kind, null, null) { Env = values } } : null,
            null, null, null);
    }
}
