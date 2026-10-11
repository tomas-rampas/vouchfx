using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using Vouchfx.Engine.Abstractions.Secrets;

namespace Vouchfx.Engine.Orchestration.Environments;

internal sealed class ComposeLease : IKeptTopology
{
    private static readonly string[] s_contextArguments = { "context", "inspect", "--format", "{{json .Endpoints.docker.Host}}" };
    private static readonly string[] s_configArguments = { "config", "--format", "json" };
    private static readonly string[] s_upArguments = { "up", "--detach", "--build" };
    private const string HealthInspectionTemplate = """
        {{json (index .Config.Labels "com.docker.compose.project")}}
        {{json (index .Config.Labels "com.docker.compose.service")}}
        {{json .State.Status}}
        {{if .State.Health}}{{json .State.Health.Status}}{{else}}null{{end}}
        """;
    private readonly ComposeConfiguration _configuration;
    private readonly IComposeProcess _process;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly string _project;
    private readonly string _journal;
    private readonly string _nativeModel;
    private readonly ComposeOwnershipRecord _ownership;
    private Task? _disposal;
    private readonly object _disposeLock = new();

    private ComposeLease(ComposeConfiguration configuration, IComposeProcess process,
        IReadOnlyDictionary<string, string> environment, string project, string journal, string nativeModel,
        ComposeOwnershipRecord ownership,
        IReadOnlyDictionary<string, object> discovered)
    {
        _configuration = configuration;
        _process = process;
        _environment = environment;
        _project = project;
        _journal = journal;
        _nativeModel = nativeModel;
        _ownership = ownership;
        DiscoveredServices = discovered.ToFrozenDictionary(StringComparer.Ordinal);
        DependencyNames = configuration.Dependencies.Keys.ToArray();
        DependencyTypes = configuration.Dependencies.ToFrozenDictionary(p => p.Key, _ => "postgres", StringComparer.Ordinal);
    }

    public EnvironmentStatePolicy StatePolicy => EnvironmentStatePolicy.FreshOwned;
    public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
    public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
    public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
    public IReadOnlyDictionary<string, object> DiscoveredServices { get; }
    public IReadOnlyList<string> DependencyNames { get; }
    public IReadOnlyDictionary<string, string> DependencyTypes { get; }
    public Task ReseedAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("Compose leases require recreation; reset and seed are unsupported.");

    internal static async Task<IKeptTopology> StartAsync(ComposeConfiguration configuration, IComposeProcess process,
        ISecretAccessor secrets, CancellationToken ct, string? journalDirectory = null)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(configuration.StartupTimeout);
        var environment = ResolveEnvironment(configuration, secrets);
        var dockerHost = System.Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrEmpty(dockerHost))
        {
            var context = await RunCheckedAsync(process, s_contextArguments,
                configuration.ProjectDirectory, environment, startup.Token).ConfigureAwait(false);
            try { dockerHost = JsonSerializer.Deserialize<string>(context); }
            catch (JsonException) { throw ComposeProcess.Failure("Could not resolve the selected Docker daemon."); }
        }
        if (!IsLocalDockerHost(dockerHost)) throw new TopologyAuthoringException("Compose bindings currently require a local Unix-socket or Windows named-pipe Docker daemon; remote TCP/SSH contexts are unsupported.");
        environment["DOCKER_HOST"] = dockerHost!;
        environment["DOCKER_CONTEXT"] = string.Empty;
        var project = "vouchfx-" + Guid.NewGuid().ToString("N");
        VerifyInputs(configuration);
        var arguments = Arguments(configuration, project);
        var json = await RunCheckedAsync(process, arguments.Concat(s_configArguments).ToArray(),
            configuration.ProjectDirectory, environment, startup.Token).ConfigureAwait(false);
        VerifyInputs(configuration);
        ValidateNativeConfiguration(json, project, configuration);
        var reloaded = await RunCheckedAsync(process, SnapshotArguments(configuration, project).Concat(s_configArguments).ToArray(),
            configuration.ProjectDirectory, environment, startup.Token, json).ConfigureAwait(false);
        // Establish native reload fidelity before mutation, including literal-dollar credentials.
        try
        {
            if (!JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(reloaded)))
                throw ComposeProcess.Failure("Native Compose cannot reload its rendered configuration unchanged. Update Docker Compose; no resources were started and subprocess output is suppressed.");
        }
        catch (JsonException) { throw ComposeProcess.Failure("Native Compose snapshot reload was malformed or truncated. No resources were started; subprocess output is suppressed."); }
        var ownership = new ComposeOwnershipRecord(1, project, dockerHost!, (int)configuration.CleanupTimeout.TotalSeconds);
        var journal = ComposeOwnershipJournal.Write(ownership, journalDirectory);
        // Native config resolves paths and escapes dollars for reloading. Retain it verbatim in memory.
        var lease = new ComposeLease(configuration, process, environment, project, journal, json, ownership, new Dictionary<string, object>());
        try
        {
            await lease.RunAsync(s_upArguments, startup.Token).ConfigureAwait(false);
            var discovered = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (name, binding) in configuration.Services)
            {
                var endpoint = await lease.DiscoverAsync(binding.Service, binding.Port, startup.Token).ConfigureAwait(false);
                if (binding.Kind == ComposeServiceKind.Http)
                {
                    var url = $"http://{endpoint.Address}:{endpoint.Port}";
                    await WaitHttpAsync(url + binding.ReadinessPath, binding.ExpectedStatus, startup.Token).ConfigureAwait(false);
                    discovered[name] = url;
                }
                else
                {
                    if (binding.Readiness == ComposeReadinessKind.Healthcheck)
                        await lease.WaitContainerHealthAsync(binding.Service, startup.Token).ConfigureAwait(false);
                    await WaitTcpAsync(endpoint, startup.Token).ConfigureAwait(false);
                    discovered[name] = endpoint.ToString();
                }
            }
            foreach (var (name, binding) in configuration.Dependencies)
            {
                var endpoint = await lease.DiscoverAsync(binding.Service, binding.Port, startup.Token).ConfigureAwait(false);
                var connection = new NpgsqlConnectionStringBuilder
                {
                    Host = endpoint.Address.ToString(),
                    Port = endpoint.Port,
                    Database = binding.Database,
                    Username = binding.Username,
                    Password = ResolveValue(binding.PasswordReference, secrets),
                    Timeout = 2,
                    CommandTimeout = 2,
                    Pooling = false,
                }.ConnectionString;
                await WaitPostgresAsync(connection, startup.Token).ConfigureAwait(false);
                discovered[name] = connection;
            }
            return new ComposeLease(configuration, process, environment, project, journal, json, ownership, discovered);
        }
        catch (Exception primary)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                throw ComposeProcess.Failure($"Compose startup failed and cleanup did not complete. Recover owned project using: vouchfx environment cleanup --journal \"{journal}\". Primary failure: {(primary is OperationCanceledException ? "startup cancelled or deadline exceeded" : "startup or readiness failed")}. No subprocess output is included.");
            }
            if (primary is ArgumentException) throw;
            if (primary is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw ComposeProcess.Failure("Compose startup or endpoint readiness failed before the deadline. Check published ports, readiness settings and credentials; subprocess output is suppressed.", OrchestrationErrorKind.HealthGate);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposal ??= CleanupAsync());
    }

    private async Task CleanupAsync()
    {
        using var cleanup = new CancellationTokenSource(_configuration.CleanupTimeout);
        await ComposeOwnedResources.CleanupAsync(_ownership, _process, _configuration.ProjectDirectory, cleanup.Token).ConfigureAwait(false);
        ComposeOwnershipJournal.Complete(_journal);
    }

    internal static async Task RecoverAsync(string journalPath, CancellationToken ct, IComposeProcess? process = null)
    {
        var record = ComposeOwnershipJournal.Read(journalPath);
        ct.ThrowIfCancellationRequested();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(record.CleanupTimeoutSeconds));
        await ComposeOwnedResources.CleanupAsync(record, process ?? new ComposeProcess(), Path.GetTempPath(), cleanup.Token).ConfigureAwait(false);
        ComposeOwnershipJournal.Complete(journalPath);
    }

    private Task<string> RunAsync(string[] command, CancellationToken ct)
        => RunCheckedAsync(_process, SnapshotArguments(_configuration, _project).Concat(command).ToArray(),
            _configuration.ProjectDirectory, _environment, ct, _nativeModel);

    private async Task<IPEndPoint> DiscoverAsync(string service, int port, CancellationToken ct)
    {
        var output = await RunAsync(new[] { "port", "--protocol", "tcp", service, port.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
        return ParsePort(output);
    }

    internal static IPEndPoint ParsePort(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1 || !IPEndPoint.TryParse(lines[0], out var endpoint)
            || endpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || endpoint.Port == 0)
            throw ComposeProcess.Failure("Selected port is absent, ambiguous or IPv6-only. Publish one TCP mapping for the selected service/port using a test override.", OrchestrationErrorKind.Discovery);
        if (endpoint.Address.Equals(IPAddress.Any)) return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        return endpoint;
    }

    internal static string[] Arguments(ComposeConfiguration configuration, string project)
    {
        var args = new List<string> { "compose", "--project-name", project, "--project-directory", configuration.ProjectDirectory };
        foreach (var file in configuration.Files) { args.Add("--file"); args.Add(file); }
        foreach (var profile in configuration.Profiles) { args.Add("--profile"); args.Add(profile); }
        foreach (var file in configuration.EnvFiles) { args.Add("--env-file"); args.Add(file); }
        return args.ToArray();
    }

    internal static bool IsLocalDockerHost(string? host)
    {
        if (host is null || host.Length > 4096) return false;
        if (host.StartsWith("npipe:////./pipe/", StringComparison.Ordinal))
        {
            var name = host["npipe:////./pipe/".Length..];
            // The server must be '.', and the pipe name is one nonempty Win32 name component.
            return name.Length is > 0 and <= 247 && name is not "." and not ".."
                && name.All(c => !char.IsControl(c) && c is not '/' and not '\\' and not '?' and not '#' and not '%');
        }
        if (!host.StartsWith("unix:///", StringComparison.Ordinal)) return false;
        var path = host["unix://".Length..];
        // Reject authority-like, relative, directory-only and URI-encoded/query forms.
        return path[1..].Split('/').All(component => component.Length > 0 && component is not "." and not ".."
            && component.All(c => !char.IsControl(c) && c is not '\\' and not '?' and not '#' and not '%'));
    }

    internal static string[] SnapshotArguments(ComposeConfiguration configuration, string project)
    {
        var args = new List<string> { "compose", "--project-name", project, "--project-directory", configuration.ProjectDirectory, "--file", "-" };
        foreach (var profile in configuration.Profiles) { args.Add("--profile"); args.Add(profile); }
        return args.ToArray();
    }

    private static Dictionary<string, string> ResolveEnvironment(ComposeConfiguration configuration, ISecretAccessor secrets)
        => configuration.Environment.ToDictionary(p => p.Key, p => ResolveValue(p.Value, secrets), StringComparer.Ordinal);

    private static void VerifyInputs(ComposeConfiguration configuration)
    {
        const string refusal = "Compose authoring inputs changed during acquisition. Retry validation/run with the current files; no resources were started.";
        ComposeConfiguration current;
        try { current = ComposeConfiguration.Load(configuration.ConfigPath); }
        catch (ArgumentException)
        {
            // Only this pure authored-input reload is typed. Do not expose its values or infer
            // authoring provenance from native process/output or post-up failures.
            throw new TopologyAuthoringException(refusal);
        }
        if (current.Fingerprint != configuration.Fingerprint)
            throw new TopologyAuthoringException(refusal);
    }

    private static string ResolveValue(string value, ISecretAccessor secrets)
    {
        if (!value.StartsWith("${secret:env/", StringComparison.Ordinal)) return value;
        try { return secrets.Resolve(value).Reveal(); }
        catch (SecretResolutionException) { throw ComposeProcess.Failure("A required Compose credential/environment reference is unavailable. Set its environment variable before running."); }
    }

    private static async Task<string> RunCheckedAsync(IComposeProcess process, IReadOnlyList<string> arguments,
        string directory, IReadOnlyDictionary<string, string> environment, CancellationToken ct, string? standardInput = null)
    {
        var result = await process.RunAsync(arguments, directory, environment, ct, standardInput).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw ComposeProcess.Failure($"Docker Compose operation failed (exit {result.ExitCode}). Check the manifest, Docker context, published-port conflicts and image/build prerequisites. Subprocess output is suppressed.");
        return result.Output;
    }

    private static async Task WaitHttpAsync(string url, int status, CancellationToken ct)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)response.StatusCode == status) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitPostgresAsync(string connectionString, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                if (Equals(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), 1)) return;
            }
            catch (NpgsqlException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitTcpAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                using var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(endpoint.Address, endpoint.Port, attempt.Token).ConfigureAwait(false);
                return;
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitContainerHealthAsync(string service, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var inventory = await RunAsync(new[] { "ps", "--all", "--quiet", service }, ct).ConfigureAwait(false);
            var ids = inventory.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids.Length != 1 || !Regex.IsMatch(ids[0], "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw ComposeProcess.Failure("Native healthcheck readiness requires exactly one owned service container; subprocess output is suppressed.", OrchestrationErrorKind.Discovery);
            var output = await RunCheckedAsync(_process,
                new[] { "container", "inspect", "--format", HealthInspectionTemplate, ids[0] },
                _configuration.ProjectDirectory, _environment, ct).ConfigureAwait(false);
            var lines = output.Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
            if (lines.Length != 4)
                throw ComposeProcess.Failure("Native healthcheck inspection was malformed or incomplete; subprocess output is suppressed.", OrchestrationErrorKind.Discovery);
            string?[] fields;
            try { fields = lines.Select(line => JsonSerializer.Deserialize<string>(line)).ToArray(); }
            catch (JsonException)
            {
                throw ComposeProcess.Failure("Native healthcheck inspection was malformed or incomplete; subprocess output is suppressed.", OrchestrationErrorKind.Discovery);
            }
            if (fields[0] != _project || fields[1] != service)
                throw ComposeProcess.Failure("Native healthcheck inspection refused a container with mismatched project/service ownership labels; subprocess output is suppressed.", OrchestrationErrorKind.Discovery);
            if (fields[2] is not ("running" or "created" or "restarting")
                || (fields[3] is not ("healthy" or "starting" or "unhealthy")
                    && !(fields[3] is null && fields[2] is "created" or "restarting")))
                throw ComposeProcess.Failure("Native healthcheck inspection found an unsupported container or health state; subprocess output is suppressed.", OrchestrationErrorKind.HealthGate);
            if (fields[2] == "running" && fields[3] == "healthy") return;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    internal static void ValidateNativeConfiguration(string json, string project, ComposeConfiguration configuration)
    {
        try { ValidateNativeConfigurationCore(json, project, configuration); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            throw ComposeProcess.Failure("Native Compose configuration has an invalid shape. No resources were started; subprocess output is suppressed.");
        }
    }

    private static void ValidateNativeConfigurationCore(string json, string project, ComposeConfiguration configuration)
    {
        using var document = ParseNativeConfiguration(json);
        var root = document.RootElement;
        void Refuse(string detail) => throw new TopologyAuthoringException("Compose ownership: " + detail + " Use an isolated test override; no resources were started.");
        if (root.TryGetProperty("secrets", out _) || root.TryGetProperty("configs", out _)) Refuse("secrets/config resources are unsupported.");
        foreach (var kind in new[] { "volumes", "networks" })
        {
            if (!root.TryGetProperty(kind, out var resources)) continue;
            foreach (var resource in resources.EnumerateObject())
            {
                var value = resource.Value;
                if (value.TryGetProperty("external", out var external) && external.GetBoolean()) Refuse("external storage/network resources are unsupported.");
                if (!value.TryGetProperty("name", out var name) || name.GetString() != project + "_" + resource.Name) Refuse("explicit/shared resource names are unsupported.");
                if (value.TryGetProperty("driver_opts", out _)) Refuse("custom storage/network driver options are unsupported.");
                if (value.TryGetProperty("driver", out var driver) && driver.GetString() != (kind == "volumes" ? "local" : "bridge")) Refuse("custom storage/network drivers are unsupported.");
                RefuseReservedLabels(value);
            }
        }
        if (!root.TryGetProperty("services", out var services)) Refuse("no services were selected.");
        foreach (var service in services.EnumerateObject())
        {
            var value = service.Value;
            if (value.TryGetProperty("provider", out _)) Refuse("delegated provider plugins are unsupported.");
            if (value.TryGetProperty("container_name", out _) || value.TryGetProperty("network_mode", out _)
                || value.TryGetProperty("volumes_from", out _) || value.TryGetProperty("external_links", out _)) Refuse("fixed container names and shared container/network namespaces are unsupported.");
            if (value.TryGetProperty("pid", out _) || value.TryGetProperty("ipc", out _) || value.TryGetProperty("uts", out _)
                || value.TryGetProperty("userns_mode", out _) || value.TryGetProperty("cgroup", out _) || value.TryGetProperty("devices", out _)
                || value.TryGetProperty("cap_add", out _) || value.TryGetProperty("use_api_socket", out _)
                || (value.TryGetProperty("privileged", out var privileged) && privileged.GetBoolean())) Refuse("host namespaces, devices, added capabilities and Docker API access are unsupported.");
            RefuseReservedLabels(value);
            if (value.TryGetProperty("volumes", out var volumes))
                foreach (var volume in volumes.EnumerateArray())
                    if (volume.TryGetProperty("type", out var type) && type.GetString() == "bind"
                        && (!volume.TryGetProperty("read_only", out var readOnly) || !readOnly.GetBoolean())) Refuse("writable host bind mounts are unsupported.");
            if (value.TryGetProperty("deploy", out var deploy) && deploy.TryGetProperty("replicas", out var replicas) && replicas.GetInt32() != 1) Refuse("replicated services are unsupported.");
            if (value.TryGetProperty("scale", out var scale) && scale.GetInt32() != 1) Refuse("replicated services are unsupported.");
        }
        foreach (var (service, port) in configuration.Services.Values.Select(b => (b.Service, b.Port))
            .Concat(configuration.Dependencies.Values.Select(b => (b.Service, b.Port))))
        {
            if (!services.TryGetProperty(service, out var value) || !value.TryGetProperty("ports", out var ports)) Refuse("a binding service/port is missing from native Compose configuration.");
            var matches = ports.EnumerateArray().Where(p => p.TryGetProperty("target", out var target) && target.GetInt32() == port
                && (!p.TryGetProperty("protocol", out var protocol) || protocol.GetString() == "tcp")).ToArray();
            if (matches.Length != 1) Refuse("a binding requires exactly one published TCP port selection.");
            if (matches[0].TryGetProperty("host_ip", out var hostIp) && hostIp.GetString() is { Length: > 0 } address
                && (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) Refuse("IPv6-only or invalid host port addresses are unsupported; publish an IPv4 test mapping.");
        }

        foreach (var binding in configuration.Services.Values.Where(b => b.Readiness == ComposeReadinessKind.Healthcheck))
        {
            var service = services.GetProperty(binding.Service);
            if (!service.TryGetProperty("healthcheck", out var healthcheck) || healthcheck.ValueKind != JsonValueKind.Object
                || (healthcheck.TryGetProperty("disable", out var disabled)
                    && (disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || disabled.GetBoolean()))
                || !healthcheck.TryGetProperty("test", out var test) || test.ValueKind != JsonValueKind.Array
                || test.GetArrayLength() < 2 || test[0].ValueKind != JsonValueKind.String
                || test[0].GetString() is not ("CMD" or "CMD-SHELL")
                || test[1].ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(test[1].GetString())
                || test.EnumerateArray().Any(argument => argument.ValueKind != JsonValueKind.String))
                Refuse("healthcheck readiness requires an enabled executable healthcheck explicitly configured for the selected native service.");
        }

        void RefuseReservedLabels(JsonElement value)
        {
            if (value.TryGetProperty("labels", out var labels)
                && labels.EnumerateObject().Any(p => p.Name.StartsWith("com.docker.compose.", StringComparison.Ordinal)))
                Refuse("reserved Compose ownership labels cannot be supplied by manifests.");
        }
    }

    private static JsonDocument ParseNativeConfiguration(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw ComposeProcess.Failure("Native Compose configuration was malformed or truncated. No resources were started; subprocess output is suppressed."); }
    }
}
