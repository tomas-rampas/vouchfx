using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration.Environments;

/// <summary>Owned, fresh Compose projects exposing explicit HTTP, TCP and PostgreSQL bindings.</summary>
public sealed class ComposeEnvironmentDriver : IEnvironmentDriver
{
    public string Id => "compose";

    /// <summary>Exports this adapter's authoring schema without binding files or invoking Docker.</summary>
    public static string ExportConfigurationSchema()
    {
        using var stream = typeof(ComposeEnvironmentDriver).Assembly.GetManifestResourceStream(
            "Vouchfx.Engine.Orchestration.Environments.compose-environment-schema.json")
            ?? throw new InvalidOperationException("Compose configuration schema was not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
    {
        var knownInputs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var configuration = ComposeConfiguration.Load(Path.GetFullPath(source.Config, suiteDirectory), knownInputs);
            return new EnvironmentPlan(configuration.Declarations(), "compose:" + configuration.Fingerprint,
                EnvironmentStatePolicy.FreshOwned,
                (context, ct) => ComposeLease.StartAsync(configuration, new ComposeProcess(), context.Secrets, ct),
                cleanupTimeout: configuration.CleanupTimeout + TimeSpan.FromSeconds(1), inputFiles: configuration.InputFiles);
        }
        catch (ArgumentException ex)
        {
            throw new EnvironmentBindingException(ex.Message, knownInputs.ToArray());
        }
    }
}
