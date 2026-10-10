using System.Text.RegularExpressions;

namespace Vouchfx.Engine.Orchestration.Environments;

/// <summary>Cleanup depends only on the recorded daemon and unpredictable project label, never live files.</summary>
internal static class ComposeOwnedResources
{
    private static readonly string[] s_kinds = { "container", "network", "volume" };
    private const string ProjectLabel = "com.docker.compose.project";

    internal static async Task CleanupAsync(ComposeOwnershipRecord record, IComposeProcess process,
        string directory, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = record.DockerHost,
            ["DOCKER_CONTEXT"] = string.Empty,
        };
        foreach (var kind in s_kinds)
        {
            foreach (var id in await ListAsync(kind).ConfigureAwait(false))
            {
                // Recheck ownership immediately before removing this exact daemon resource.
                var template = kind == "container" ? "{{index .Config.Labels \"" + ProjectLabel + "\"}}"
                    : "{{index .Labels \"" + ProjectLabel + "\"}}";
                var owner = await RunAsync(new[] { kind, "inspect", "--format", template, id }).ConfigureAwait(false);
                if (owner.Trim() != record.Project)
                    throw ComposeProcess.Failure("Compose cleanup refused a resource whose project ownership label changed. The journal was retained; inspect the owned project before recovery.");
                var arguments = kind == "container"
                    ? new[] { kind, "rm", "--force", "--volumes", id }
                    : new[] { kind, "rm", id };
                await RunAsync(arguments).ConfigureAwait(false);
            }
        }
        foreach (var kind in s_kinds)
            if ((await ListAsync(kind).ConfigureAwait(false)).Length != 0)
                throw ComposeProcess.Failure("Compose cleanup left project-labelled resources. The ownership journal was retained for recovery.");

        async Task<string> RunAsync(string[] arguments)
        {
            var result = await process.RunAsync(arguments, directory, environment, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw ComposeProcess.Failure($"Compose owned-resource cleanup failed (exit {result.ExitCode}). The journal was retained; recover with vouchfx environment cleanup --journal. Subprocess output is suppressed.");
            return result.Output;
        }

        async Task<string[]> ListAsync(string kind)
        {
            var arguments = new List<string> { kind, "ls", "--quiet" };
            if (kind != "volume") arguments.Add("--no-trunc");
            if (kind == "container") arguments.Add("--all");
            arguments.Add("--filter");
            arguments.Add("label=" + ProjectLabel + "=" + record.Project);
            var output = await RunAsync(arguments.ToArray()).ConfigureAwait(false);
            var ids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids.Length > 1024 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
                || ids.Any(id => kind == "volume"
                    ? !id.StartsWith(record.Project + "_", StringComparison.Ordinal)
                        || !Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")
                    : !Regex.IsMatch(id, "^[a-f0-9]{64}$")))
                throw ComposeProcess.Failure("Compose cleanup received an invalid or unexpected owned-resource inventory. No unverified resource was removed; the journal was retained.");
            return ids;
        }
    }
}
