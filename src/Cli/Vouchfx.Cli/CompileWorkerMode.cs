using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Compilation;

namespace Vouchfx.Cli;

// One request, one response, no script execution. Never forward child stderr or exception text.
internal static class CompileWorkerMode
{
    private const int MaximumCharacters = 32 * 1024 * 1024;
    internal sealed record Request(string Source, string[] ReferencePaths);
    internal sealed record Response(string Status, byte[]? Image = null);

    internal static async Task<int> ExecuteAsync()
    {
        try
        {
            var input = await ReadBoundedAsync(Console.In, CancellationToken.None);
            var request = JsonSerializer.Deserialize<Request>(input);
            if (request is null || request.Source is null || request.ReferencePaths is null)
                return 2;
            Response response;
            try
            {
                var compiled = RoslynScriptCompiler.CompileOnce(request.Source,
                    additionalReferencePaths: request.ReferencePaths);
                response = new Response("ok", compiled.Image);
            }
            catch (ScriptCompilationException)
            {
                response = new Response("diagnostics");
            }
            await Console.Out.WriteAsync(JsonSerializer.Serialize(response));
            return 0;
        }
        catch (Exception)
        {
            return 2;
        }
    }

    internal static CompiledScript Compile(string source, IReadOnlyList<string>? references, CancellationToken token) =>
        CompileAsync(source, references, token).GetAwaiter().GetResult();

    internal static async Task<CompiledScript> CompileAsync(string source, IReadOnlyList<string>? references, CancellationToken token,
        Action<int>? startedCallback = null, TimeSpan? compileBudget = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(compileBudget ?? RoslynScriptCompiler.DefaultCompileBudget);
        var useAppHost = Assembly.GetEntryAssembly() == typeof(CompileWorkerMode).Assembly
            && !string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = useAppHost ? Environment.ProcessPath!
                    : string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
                        ? Environment.ProcessPath! : ResolveDotnetHost(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        if (!useAppHost)
            process.StartInfo.ArgumentList.Add(typeof(CompileWorkerMode).Assembly.Location);
        process.StartInfo.ArgumentList.Add("--compile-worker");
        process.StartInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        var started = false;
        Task? outputTask = null;
        Task? errorTask = null;
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            startedCallback?.Invoke(process.Id);
            var output = ReadBoundedAsync(process.StandardOutput, budget.Token);
            var errors = DrainAsync(process.StandardError, budget.Token);
            outputTask = output;
            errorTask = errors;
            var request = JsonSerializer.Serialize(new Request(source, references?.ToArray() ?? Array.Empty<string>()));
            if (request.Length > MaximumCharacters)
                throw new CompileWorkerException("Compile worker request exceeds the size limit.");
            await process.StandardInput.WriteAsync(request.AsMemory(), budget.Token);
            process.StandardInput.Close();
            var envelope = await output;
            await errors;
            await process.WaitForExitAsync(budget.Token);
            if (process.ExitCode != 0)
                throw new CompileWorkerException("Compile worker terminated without a valid result.");
            var response = JsonSerializer.Deserialize<Response>(envelope);
            if (response is { Status: "ok", Image.Length: > 0 })
                return new CompiledScript(response.Image);
            if (response is { Status: "diagnostics", Image: null })
                throw new ScriptCompilationException(new[]
                {
                    Microsoft.CodeAnalysis.Diagnostic.Create(new Microsoft.CodeAnalysis.DiagnosticDescriptor(
                        "VFXCOMP001", "Script compilation", "Script compilation failed in the isolated worker.",
                        "Compilation", Microsoft.CodeAnalysis.DiagnosticSeverity.Error, true), Microsoft.CodeAnalysis.Location.None),
                });
            throw new CompileWorkerException("Compile worker returned an invalid result.");
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            throw new CompileWorkerException("Compile worker was cancelled or exceeded its compile budget.");
        }
        catch (CompileWorkerException) { throw; }
        catch (ScriptCompilationException) { throw; }
        catch (Exception)
        {
            throw new CompileWorkerException("Compile worker could not produce a valid result.");
        }
        finally
        {
            // Use the shared, parity-tested kill guard on every path, before disposing the handle.
            Selection.SystemProcessRunner.KillTreeQuietly(process);
            if (started)
            {
                try
                {
                    await ConfirmExitAsync(process);
                }
                finally
                {
                    budget.Cancel();
                    try { await Task.WhenAll(outputTask ?? Task.CompletedTask, errorTask ?? Task.CompletedTask); }
                    catch (Exception) { /* Output is deliberately discarded on failure. */ }
                }
            }
        }
    }

    private static async Task ConfirmExitAsync(Process process)
    {
        try
        {
            using var exitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exitBudget.Token);
        }
        catch (Exception)
        {
            throw new CompileWorkerException("Compile worker exit could not be confirmed.");
        }
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (configured is not null && Path.IsPathFullyQualified(configured) && File.Exists(configured))
            return configured;
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var host = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (!File.Exists(host))
            throw new CompileWorkerException("Compile worker requires an absolute .NET host path.");
        return host;
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > MaximumCharacters)
                throw new CompileWorkerException("Compile worker exceeded its response size limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static async Task DrainAsync(TextReader reader, CancellationToken token)
    {
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }
}
