using System.CommandLine;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;

namespace Vouchfx.Cli;

internal static class EnvironmentCommand
{
    internal static Command Build()
    {
        var command = new Command("environment", "Manage owned environment recovery records.");
        var cleanup = new Command("cleanup", "Clean up one interrupted owned Compose project using its ownership record.");
        var journal = new Option<string>("--journal") { Required = true, Description = "Ownership JSON record under the local vouchfx/environments directory." };
        cleanup.Add(journal);
        cleanup.SetAction(async (result, ct) =>
        {
            try
            {
                await EnvironmentRecovery.CleanupAsync(result.GetRequiredValue(journal), ct).ConfigureAwait(false);
                Console.Out.WriteLine("Owned Compose environment cleanup completed.");
                return ExitCodes.Success;
            }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return ExitCodes.UsageError; }
            catch (Exception ex) when (ex is OrchestrationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
            {
                Console.Error.WriteLine("Owned environment cleanup did not complete. Check the retained ownership record and access to its recorded local Docker daemon, then retry; original manifests and credentials are not required.");
                return ExitCodes.EnvironmentError;
            }
        });
        command.Add(cleanup);
        return command;
    }
}
