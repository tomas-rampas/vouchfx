using System.Diagnostics;
using System.Text;

namespace Vouchfx.Engine.Orchestration.Environments;

internal sealed record ComposeProcessResult(int ExitCode, string Output);
internal interface IComposeProcess
{
    Task<ComposeProcessResult> RunAsync(IReadOnlyList<string> arguments, string directory,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, string? standardInput = null);
}

/// <summary>Argument-list execution with bounded output; credential-bearing output never leaves the adapter.</summary>
internal sealed class ComposeProcess : IComposeProcess
{
    public async Task<ComposeProcessResult> RunAsync(IReadOnlyList<string> arguments, string directory,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, string? standardInput = null)
    {
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("COMPOSE_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw Failure("Docker CLI could not start.");
            var stdout = ReadBoundedAsync(process.StandardOutput, retain: true, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, retain: false, cancellationToken);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            return new ComposeProcessResult(process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw Failure("Docker CLI execution failed or exceeded its 8 MiB output limit. Check Docker/Compose installation and input size."); }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, bool retain, CancellationToken ct)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        var count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            count += read;
            if (count > 8 * 1024 * 1024) throw new IOException("Output limit exceeded.");
            if (retain) output.Append(buffer, 0, read);
        }
        return output.ToString();
    }

    internal static OrchestrationException Failure(string detail, OrchestrationErrorKind kind = OrchestrationErrorKind.Provision)
        => new(new OrchestrationErrorInfo(kind, "compose", null, null, detail));
}
