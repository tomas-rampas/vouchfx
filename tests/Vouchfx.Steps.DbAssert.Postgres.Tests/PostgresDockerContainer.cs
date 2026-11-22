// A throwaway PostgreSQL container driven through the docker CLI, for the Docker-backed pins.
//
// Why the CLI and not the engine's Aspire topology: the pins need a server started with TLS
// settings and certificate files, and the engine's environment grammar has no container command or
// file-injection field for a postgres dependency. The docker CLI is also what the repository's other
// Docker-backed fixtures use for inspection and teardown, with ChildProcess.KillTreeQuietly as the one
// guarded tree kill. Nothing here is product code.
//
// Teardown: every container carries the label below and is removed with `docker rm -f -v` (the
// -v also removes the postgres image's anonymous data volume), on dispose and on a failed start.
// A test process killed before dispose leaves its containers behind; they are named
// vouchfx-pgpin-* and labelled, so `docker ps -a --filter label=...` finds them.
//
// The generated password is handed to docker through the child process's environment (`-e NAME`
// with no value), so it never appears in a docker argument list that `ps` can show.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Vouchfx.TestSupport;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

internal sealed class PostgresDockerContainer : IAsyncDisposable
{
    /// <summary>The image the engine's postgres dependency runs (the Aspire pin).</summary>
    public const string Image = "postgres:18.3";

    public const string Label = "vouchfx.test=db-assert-postgres-npgsql-pins";

    private static readonly TimeSpan s_commandTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromSeconds(120);

    private const string PasswordVariable = "POSTGRES_PASSWORD";

    private readonly string _password;

    private PostgresDockerContainer(string name, int port, string password)
    {
        Name = name;
        Port = port;
        _password = password;
    }

    public string Name { get; }

    public int Port { get; }

    /// <summary>Connection string for the container, with <paramref name="extra"/> appended (start it with ';').</summary>
    public string ConnectionString(string extra = "") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Host=127.0.0.1;Port={Port};Username=postgres;Password={_password};Database=postgres;Pooling=false{extra}");

    /// <summary>Starts a plain (non-TLS) server.</summary>
    public static Task<PostgresDockerContainer> StartPlainAsync() => StartAsync(null, null);

    /// <summary>Starts a server with <c>ssl=on</c>, using the given certificate chain and private key PEM files.</summary>
    public static Task<PostgresDockerContainer> StartTlsAsync(string certificatePemPath, string keyPemPath) =>
        StartAsync(certificatePemPath, keyPemPath);

    private static async Task<PostgresDockerContainer> StartAsync(string? certificatePath, string? keyPath)
    {
        var name = "vouchfx-pgpin-" + Guid.NewGuid().ToString("N")[..10];
        var password = Guid.NewGuid().ToString("N");
        var tls = certificatePath is not null;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { [PasswordVariable] = password };
        try
        {
            if (tls)
            {
                // The key must be owned by the postgres user with mode 0600, which a file copied in as
                // root is not; the container's own command installs both files before the server starts.
                const string script =
                    "mkdir -p /tls && " +
                    "install -m 0644 -o postgres -g postgres /tmp/server.crt /tls/server.crt && " +
                    "install -m 0600 -o postgres -g postgres /tmp/server.key /tls/server.key && " +
                    "exec docker-entrypoint.sh postgres -c ssl=on " +
                    "-c ssl_cert_file=/tls/server.crt -c ssl_key_file=/tls/server.key";
                await DockerAsync(
                    environment, true,
                    "create", "--name", name, "--label", Label, "-e", PasswordVariable,
                    "-p", "127.0.0.1::5432", "--entrypoint", "bash", Image, "-c", script);
                await DockerAsync("cp", certificatePath!, name + ":/tmp/server.crt");
                await DockerAsync("cp", keyPath!, name + ":/tmp/server.key");
                await DockerAsync("start", name);
            }
            else
            {
                await DockerAsync(
                    environment, true,
                    "run", "-d", "--name", name, "--label", Label, "-e", PasswordVariable,
                    "-p", "127.0.0.1::5432", Image);
            }

            var mapping = (await DockerAsync("port", name, "5432/tcp")).StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            var port = int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);

            var deadline = DateTime.UtcNow + s_readyTimeout;
            while (true)
            {
                var ready = await DockerAsync(false, "exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres");
                if (ready.ExitCode == 0)
                {
                    break;
                }

                if (DateTime.UtcNow > deadline)
                {
                    var logs = (await DockerAsync(false, "logs", "--tail", "40", name)).StdErr;
                    throw new InvalidOperationException(
                        "PostgreSQL container did not become ready within the timeout. Last log lines:\n" + logs);
                }

                await Task.Delay(500);
            }

            return new PostgresDockerContainer(name, port, password);
        }
        catch
        {
            try
            {
                await DockerAsync(false, "rm", "-f", "-v", name);
            }
            catch (Exception cleanup) when (cleanup is Win32Exception or OperationCanceledException or InvalidOperationException)
            {
                // A failed cleanup must not replace the start failure being rethrown.
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync() => await DockerAsync(false, "rm", "-f", "-v", Name);

    private static Task<DockerResult> DockerAsync(params string[] arguments) => DockerAsync(null, true, arguments);

    private static Task<DockerResult> DockerAsync(bool throwOnFailure, params string[] arguments) =>
        DockerAsync(null, throwOnFailure, arguments);

    private static async Task<DockerResult> DockerAsync(
        IReadOnlyDictionary<string, string>? environment, bool throwOnFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var timeout = new CancellationTokenSource(s_commandTimeout);
        using var process = Process.Start(startInfo)!;
        try
        {
            // Drain both pipes concurrently so a full stderr buffer cannot block the child.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var result = new DockerResult(process.ExitCode, await stdout, await stderr);
            if (throwOnFailure && result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"docker {arguments[0]} failed with exit code {result.ExitCode}: {result.StdErr}");
            }

            return result;
        }
        finally
        {
            ChildProcess.KillTreeQuietly(process);
        }
    }

    private sealed record DockerResult(int ExitCode, string StdOut, string StdErr);
}
