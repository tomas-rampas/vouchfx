// Census gate for the dependency-env feature (spec REQ-003).
//
// REQ-003 promoted this to a MERGE GATE: T2 made `env` legal on all thirteen dependency types in a
// frozen, additive-only schema, which is correct only if every type is container-backed. `env`
// cannot be narrowed off a type inside v1.x, so a type that cannot take it would become a
// permanent no-op the schema is obliged to keep accepting. This test is the measurement.
//
// It enumerates the type list from the SCHEMA, not from a literal here, so adding a fourteenth
// dependency type without wiring the env seam turns this red rather than leaving it silently
// unmeasured.
//
// Non-Docker: Map + Configure build the resource graph in memory, and the
// EnvironmentCallbackAnnotation callbacks resolve without any live endpoint. See the test-strategy
// note at the top of EnvironmentMapperTests.cs.
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Asserts that every dependency <c>type</c> the JSON Schema accepts maps to a resource that
/// actually receives an author's <c>env:</c> mapping.
/// </summary>
public sealed class DependencyEnvCensusTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        });

    public static TheoryData<string> DependencyTypes()
    {
        var data = new TheoryData<string>();
        foreach (var t in SchemaDependencyTypeEnum())
        {
            data.Add(t);
        }

        return data;
    }

    /// <summary>
    /// Reads <c>$defs/dependency/properties/type/enum</c> out of the embedded
    /// <c>root-language-schema.json</c> — the same resource the validator composes from, so the
    /// census cannot drift from the language the schema accepts.
    /// </summary>
    private static List<string> SchemaDependencyTypeEnum()
    {
        var assembly = typeof(Vouchfx.Engine.Compilation.Schema.SchemaComposer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("root-language-schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var doc = JsonDocument.Parse(stream);
        var names = doc.RootElement
            .GetProperty("$defs").GetProperty("dependency")
            .GetProperty("properties").GetProperty("type")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
        Assert.NotEmpty(names);
        return names;
    }

    [Theory]
    [MemberData(nameof(DependencyTypes))]
    public async Task EveryDependencyType_AppliesAuthorEnvToItsOwnContainer(string type)
    {
        var probeValue = "probe-" + type;
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["dep"] = new DependencySpec(type, null, ExtraFor(type))
                {
                    Env = new Dictionary<string, string> { ["VOUCHFX_CENSUS_PROBE"] = probeValue },
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        // Exactly one container carries the declared name: the two sidecar-bearing types name
        // theirs '<dep>-sr' (kafka schema registry) and '<dep>-sqledge' (azureservicebus SQL), so
        // Single() also pins that neither is ever mistaken for the dependency itself.
        var target = Assert.Single(
            builder.Resources.OfType<ContainerResource>(), r => r.Name == "dep");

        var vars = await ResolveEnvVarsAsync(target);
        Assert.True(
            vars.ContainsKey("VOUCHFX_CENSUS_PROBE"),
            $"Dependency type '{type}' did not receive the author's env: variable. "
            + $"Resolved keys: {string.Join(", ", vars.Keys)}");
        Assert.Equal(probeValue, ValueTextOf(vars["VOUCHFX_CENSUS_PROBE"]));

        // The author configured the dependency, not its internal sidecars — those carry names no
        // author can write, so a variable landing on one would be an engine-authored surprise.
        foreach (var sidecar in builder.Resources.Where(r => r.Name != "dep"))
        {
            var sidecarVars = await ResolveEnvVarsAsync(sidecar);
            Assert.False(
                sidecarVars.ContainsKey("VOUCHFX_CENSUS_PROBE"),
                $"Dependency type '{type}': the author's env: variable leaked onto "
                + $"'{sidecar.Name}', which is not the declared dependency's own resource.");
        }
    }

    /// <summary>
    /// kafka provisions its schema-registry sidecar only when <c>schemaRegistry: true</c>, and the
    /// sidecar is the case worth covering — so the kafka row of the census opts into it.
    /// </summary>
    private static YamlMappingNode? ExtraFor(string type) =>
        type == "kafka"
            ? new YamlMappingNode
            {
                { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
            }
            : null;

    private static string ValueTextOf(object envVarValue) => envVarValue switch
    {
        ReferenceExpression re => re.ValueExpression,
        string s => s,
        _ => envVarValue.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Runs every registered <see cref="EnvironmentCallbackAnnotation"/> on
    /// <paramref name="resource"/> and returns the populated environment-variables dictionary —
    /// the same in-memory technique <c>EnvironmentMapperTests.ResolveEnvVarsAsync</c> uses.
    /// </summary>
    private static async Task<Dictionary<string, object>> ResolveEnvVarsAsync(IResource resource)
    {
        var envVars = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resource,
            envVars);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(callbackContext);
        }

        return envVars;
    }
}
