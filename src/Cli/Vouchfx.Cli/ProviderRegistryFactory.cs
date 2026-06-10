// Vouchfx.Cli — ProviderRegistryFactory (S07-C-01).
//
// The ONE place in the CLI that names the Core provider assemblies. The runner is
// deliberately provider-agnostic (it takes the assemblies to scan as a parameter), so
// this factory is the single seam where the CLI declares which providers it ships with.
// Adding a provider to the CLI is a one-line change here (plus the matching
// ProjectReference in Vouchfx.Cli.csproj).
//
// webhook-listen.http is added in a later task; the // TODO below marks the slot.

using System.Reflection;
using Platform.Sdk;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.HttpRest;
using Platform.Steps.MqExpect.Kafka;
using Platform.Steps.MqPublish.Kafka;
using Platform.Steps.Script.Csharp;

namespace Vouchfx.Cli;

/// <summary>
/// Builds the set of Core provider assemblies the CLI ships with, and freezes them
/// into a <see cref="StepKindRegistry"/>.
/// </summary>
/// <remarks>
/// This is the single point of truth for which providers the <c>vouchfx</c> executable
/// bundles.  It is referenced anchor-by-anchor (one concrete provider type per assembly)
/// rather than by string name so a renamed/removed provider is a compile error here, not
/// a silent runtime gap.
/// </remarks>
internal static class ProviderRegistryFactory
{
    /// <summary>
    /// Returns the assemblies the CLI scans for <c>[StepProvider]</c>-decorated providers.
    /// </summary>
    /// <remarks>
    /// One anchor type per Core provider assembly.  The five Core providers wired this
    /// sprint are <c>http.rest</c>, <c>db-assert.postgres</c>, <c>script.csharp</c>,
    /// <c>mq-publish.kafka</c> and <c>mq-expect.kafka</c>.  <c>webhook-listen.http</c>
    /// joins the list in a later task — see the TODO below.
    /// </remarks>
    public static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        // TODO(S08+): typeof(WebhookListenHttpProvider).Assembly — webhook-listen.http.
    };

    /// <summary>
    /// Builds and freezes the <see cref="StepKindRegistry"/> from the CLI's Core provider
    /// assemblies.
    /// </summary>
    /// <returns>A frozen registry containing every Core provider this CLI ships with.</returns>
    public static StepKindRegistry BuildCoreRegistry() =>
        StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());
}
