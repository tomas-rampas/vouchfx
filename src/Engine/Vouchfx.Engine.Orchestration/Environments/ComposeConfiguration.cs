using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration.Environments;

internal sealed record ComposeHttpBinding(string Service, int Port, string ReadinessPath, int ExpectedStatus);
internal sealed record ComposePostgresBinding(string Service, int Port, string Database, string Username, string PasswordReference);

/// <summary>The adapter's configuration. Native Compose alone interprets and merges manifests.</summary>
internal sealed record ComposeConfiguration(
    string ConfigPath,
    string ProjectDirectory,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> EnvFiles,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, ComposeHttpBinding> Services,
    IReadOnlyDictionary<string, ComposePostgresBinding> Dependencies,
    TimeSpan StartupTimeout,
    TimeSpan CleanupTimeout,
    string Fingerprint)
{
    private static readonly string[] s_httpEndpoints = { "http" };
    internal IReadOnlyList<string> InputFiles { get; init; } = Array.Empty<string>();
    internal static ComposeConfiguration Load(string path, ISet<string>? knownInputs = null)
    {
        var configPath = Path.GetFullPath(path);
        var inputFiles = knownInputs ?? new HashSet<string>(StringComparer.Ordinal);
        EnvironmentBindingException.TrackInput(inputFiles, configPath);
        var configDirectory = Path.GetDirectoryName(configPath)!;
        var bytes = ReadInput(configPath);
        var yaml = new YamlStream();
        try { yaml.Load(new StringReader(Encoding.UTF8.GetString(bytes))); }
        catch (YamlDotNet.Core.YamlException) { throw new ArgumentException("Compose configuration: invalid YAML. Check its syntax and duplicate keys."); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("Compose configuration must contain one mapping document.");

        var reader = new ConfigMapping(root, "compose");
        reader.Only("version", "files", "projectDirectory", "profiles", "envFiles", "environment", "services", "dependencies", "startupTimeoutSeconds", "cleanupTimeoutSeconds");
        if (reader.Integer("version", 1, 1) != 1) throw reader.Error("version", "must be 1");
        string ResolveInput(string directory, string value)
        {
            var input = Resolve(directory, value);
            EnvironmentBindingException.TrackInput(inputFiles, input);
            return input;
        }
        var files = reader.StringList("files", required: true).Select(p => ResolveInput(configDirectory, p)).ToArray();
        if (files.Length == 0 || files.Distinct(StringComparer.Ordinal).Count() != files.Length)
            throw reader.Error("files", "requires a nonempty ordered list of distinct manifest paths");
        var projectDirectory = reader.OptionalText("projectDirectory") is { } directory
            ? Resolve(configDirectory, directory) : Path.GetDirectoryName(files[0])!;
        if (!Directory.Exists(projectDirectory)) throw reader.Error("projectDirectory", "directory does not exist");
        var profiles = reader.StringList("profiles").ToArray();
        foreach (var profile in profiles) Name(profile, reader, "profiles");
        var envFiles = reader.StringList("envFiles").Select(p => ResolveInput(configDirectory, p)).ToArray();
        var implicitEnv = envFiles.Length == 0 ? ResolveInput(projectDirectory, ".env") : null;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in reader.Map("environment"))
        {
            if (!EnvironmentVariableNames.IsValid(name)) throw reader.Error("environment", EnvironmentVariableNames.SyntaxMessage);
            if (name.StartsWith("COMPOSE_", StringComparison.Ordinal) || name.StartsWith("DOCKER_", StringComparison.Ordinal))
                throw reader.Error("environment", "cannot override Compose ownership selections or Docker daemon configuration");
            environment[name] = ConfigMapping.Scalar(value, reader, "environment." + name);
            ValidateReference(environment[name], reader, "environment." + name, required: false);
        }

        var services = new Dictionary<string, ComposeHttpBinding>(StringComparer.Ordinal);
        foreach (var (name, node) in reader.Map("services"))
        {
            Name(name, reader, "services");
            var item = ConfigMapping.Child(node, "services." + name);
            item.Only("service", "port", "readiness");
            var service = item.Text("service");
            Name(service, item, "service");
            var readiness = item.Child("readiness");
            readiness.Only("path", "status");
            var readinessPath = readiness.Text("path");
            if (!readinessPath.StartsWith('/') || readinessPath.StartsWith("//", StringComparison.Ordinal)
                || readinessPath.Contains('?') || readinessPath.Contains('#') || readinessPath.Contains("${", StringComparison.Ordinal))
                throw readiness.Error("path", "must be an HTTP path without authority, query, fragment or credentials");
            services.Add(name, new ComposeHttpBinding(service, item.Integer("port", 1, 65535),
                readinessPath, readiness.Integer("status", 100, 599, 200)));
        }

        var dependencies = new Dictionary<string, ComposePostgresBinding>(StringComparer.Ordinal);
        foreach (var (name, node) in reader.Map("dependencies"))
        {
            Name(name, reader, "dependencies");
            if (services.ContainsKey(name)) throw reader.Error("dependencies." + name, "cannot share a logical name with a service");
            var item = ConfigMapping.Child(node, "dependencies." + name);
            item.Only("type", "service", "port", "database", "username", "password");
            if (item.Text("type") != "postgres") throw item.Error("type", "only postgres is supported in this Compose slice");
            var service = item.Text("service");
            Name(service, item, "service");
            var password = item.Text("password");
            ValidateReference(password, item, "password", required: true);
            dependencies.Add(name, new ComposePostgresBinding(service, item.Integer("port", 1, 65535),
                item.Text("database"), item.Text("username"), password));
        }
        if (services.Count + dependencies.Count == 0) throw reader.Error("services", "declare at least one HTTP or PostgreSQL binding");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddHash(hash, configPath, bytes);
        foreach (var input in files.Concat(envFiles).Distinct(StringComparer.Ordinal))
        {
            var inputBytes = ReadInput(input);
            AddHash(hash, input, inputBytes);
            if (files.Contains(input, StringComparer.Ordinal))
                foreach (var referenced in ManifestEnvironmentFiles(inputBytes, projectDirectory,
                    input => EnvironmentBindingException.TrackInput(inputFiles, input)).Distinct(StringComparer.Ordinal))
                {
                    AddHash(hash, referenced, ReadInput(referenced));
                }
        }
        if (implicitEnv is not null)
        {
            AddHash(hash, implicitEnv, File.Exists(implicitEnv) ? ReadInput(implicitEnv) : Array.Empty<byte>());
        }
        return new ComposeConfiguration(configPath, projectDirectory, files, profiles, envFiles,
            environment.ToFrozenDictionary(StringComparer.Ordinal), services.ToFrozenDictionary(StringComparer.Ordinal),
            dependencies.ToFrozenDictionary(StringComparer.Ordinal),
            TimeSpan.FromSeconds(reader.Integer("startupTimeoutSeconds", 1, 600, 120)),
            TimeSpan.FromSeconds(reader.Integer("cleanupTimeoutSeconds", 1, 120, 30)),
            Convert.ToHexString(hash.GetHashAndReset()))
        { InputFiles = Array.AsReadOnly(inputFiles.Order(StringComparer.Ordinal).ToArray()) };
    }

    internal EnvironmentDeclarations Declarations() => new(
        Services.ToDictionary(p => p.Key, _ => new DeclaredServiceInfo(s_httpEndpoints), StringComparer.Ordinal),
        Dependencies.ToDictionary(p => p.Key, _ => "postgres", StringComparer.Ordinal));

    private static List<string> ManifestEnvironmentFiles(byte[] bytes, string projectDirectory, Action<string> trackInput)
    {
        var manifest = new YamlStream();
        try { manifest.Load(new StringReader(Encoding.UTF8.GetString(bytes))); }
        catch (YamlDotNet.Core.YamlException) { throw new ArgumentException("Compose manifest: invalid YAML; check syntax and duplicate keys."); }
        var referenced = new List<string>();
        var mappings = new ManifestInputMappings();
        foreach (var document in manifest.Documents)
        {
            if (document.RootNode is not YamlMappingNode root) continue;
            var rootProperties = mappings.Read(root);
            if (rootProperties.ContainsKey("include")) RefuseUntrackedFormat();
            if (!rootProperties.TryGetValue("services", out var services) || services is not YamlMappingNode serviceMap) continue;
            foreach (var service in mappings.Read(serviceMap).Values)
            {
                if (service is not YamlMappingNode serviceNode) continue;
                var properties = mappings.Read(serviceNode);
                if (properties.ContainsKey("extends")) RefuseUntrackedFormat();
                if (!properties.TryGetValue("env_file", out var value)) continue;
                IEnumerable<YamlNode> entries = value is YamlSequenceNode list ? list.Children : new[] { value };
                foreach (var entry in entries)
                {
                    var path = entry is YamlScalarNode { Value: { Length: > 0 } text } ? text
                        : ConfigMapping.Child(entry, "compose.services.env_file").Text("path");
                    var input = Resolve(projectDirectory, path);
                    trackInput(input);
                    referenced.Add(input);
                }
            }
        }
        return referenced;

        static void RefuseUntrackedFormat()
            => throw new ArgumentException("Compose manifest include/extends is unsupported in this slice. List complete test manifests in files; no resources were started.");
    }

    /// <summary>Reads YAML mapping inheritance only to locate authoring inputs; native Compose still merges manifests.</summary>
    private sealed class ManifestInputMappings
    {
        private readonly Dictionary<YamlMappingNode, IReadOnlyDictionary<string, YamlNode>> _cache = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<YamlMappingNode> _active = new(ReferenceEqualityComparer.Instance);
        private int _visits;

        internal IReadOnlyDictionary<string, YamlNode> Read(YamlMappingNode mapping, int depth = 0)
        {
            if (depth > 64 || _cache.Count + _active.Count >= 4096)
                throw new ArgumentException("Compose manifest YAML mapping inheritance exceeds the bounded input-tracking limit. Simplify mapping anchors; no resources were started.");
            if (_cache.TryGetValue(mapping, out var cached)) return cached;
            if (!_active.Add(mapping))
                throw new ArgumentException("Compose manifest has cyclic YAML mapping inheritance. Remove the merge cycle; no resources were started.");
            try
            {
                var properties = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
                // Explicit properties override inherited values, independent of their position.
                foreach (var (key, value) in mapping.Children)
                {
                    VisitProperty();
                    if (key is YamlScalarNode { Value: { } name } && !IsMergeKey(key)) properties.Add(name, value);
                }
                foreach (var (key, value) in mapping.Children)
                {
                    if (!IsMergeKey(key)) continue;
                    IEnumerable<YamlNode> sources = value is YamlSequenceNode sequence ? sequence.Children : new[] { value };
                    foreach (var source in sources)
                    {
                        if (source is not YamlMappingNode inherited)
                            throw new ArgumentException("Compose manifest YAML merge inputs must be mappings; no resources were started.");
                        foreach (var property in Read(inherited, depth + 1))
                        {
                            VisitProperty();
                            // Earlier sequence entries take precedence, as in native YAML merges.
                            properties.TryAdd(property.Key, property.Value);
                        }
                    }
                }
                _cache.Add(mapping, properties);
                return properties;
            }
            finally { _active.Remove(mapping); }
        }

        private void VisitProperty()
        {
            if (++_visits > 100_000)
                throw new ArgumentException("Compose manifest YAML mapping inheritance exceeds the bounded input-tracking limit. Simplify mapping anchors; no resources were started.");
        }

        private static bool IsMergeKey(YamlNode key)
            => key is YamlScalarNode { Value: "<<" } scalar
                && (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain || scalar.Tag.ToString() == "tag:yaml.org,2002:merge");
    }

    private static string Resolve(string directory, string value)
    {
        if (value.Contains("${", StringComparison.Ordinal)) throw new ArgumentException("Compose configuration paths cannot contain placeholders or secrets.");
        return Path.GetFullPath(value, directory);
    }

    private static byte[] ReadInput(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new ArgumentException("Compose input exceeds the 4 MiB authoring limit.");
            return File.ReadAllBytes(path);
        }
        catch (IOException) { throw new ArgumentException("Compose configuration references a missing or unreadable input file."); }
        catch (UnauthorizedAccessException) { throw new ArgumentException("Compose configuration references an unreadable input file."); }
    }

    private static void AddHash(IncrementalHash hash, string path, byte[] bytes)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path);
        hash.AppendData(BitConverter.GetBytes(pathBytes.Length));
        hash.AppendData(pathBytes);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static void Name(string name, ConfigMapping reader, string key)
    {
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")) throw reader.Error(key, "requires a nonempty logical/service identifier");
    }

    private static void ValidateReference(string value, ConfigMapping reader, string key, bool required)
    {
        var valid = Regex.IsMatch(value, @"^\$\{secret:env/[A-Za-z_][A-Za-z0-9_]*\}$");
        if ((required && !valid) || (!valid && value.Contains("${", StringComparison.Ordinal)))
            throw reader.Error(key, "use an entire ${secret:env/VARIABLE} reference; other secret sources and interpolation are unsupported");
    }
}

internal sealed class ConfigMapping
{
    private readonly Dictionary<string, YamlNode> _values = new(StringComparer.Ordinal);
    private readonly YamlMappingNode _node;
    private readonly string _path;

    internal ConfigMapping(YamlMappingNode node, string path)
    {
        _node = node;
        _path = path;
        foreach (var (key, value) in node.Children)
        {
            if (key is not YamlScalarNode { Value: { } text } || !_values.TryAdd(text, value))
                throw Error("", "requires distinct string keys");
        }
    }

    internal ArgumentException Error(string key, string message)
        => new($"{_path}{(key.Length == 0 ? "" : "." + key)} (line {_node.Start.Line + 1}): {message}.");

    internal void Only(params string[] keys)
    {
        var unknown = _values.Keys.FirstOrDefault(k => !keys.Contains(k, StringComparer.Ordinal));
        if (unknown is not null) throw Error(unknown, "unsupported configuration key; security, seed, borrowed resources and reuse are not supported");
    }

    internal string Text(string key) => OptionalText(key) ?? throw Error(key, "is required");
    internal string? OptionalText(string key) => _values.TryGetValue(key, out var node) ? Scalar(node, this, key) : null;
    internal static string Scalar(YamlNode node, ConfigMapping reader, string key)
        => node is YamlScalarNode { Value: { Length: > 0 } text } ? text : throw reader.Error(key, "requires a nonempty scalar string");
    internal int Integer(string key, int min, int max, int? defaultValue = null)
    {
        var text = OptionalText(key);
        if (text is null && defaultValue.HasValue) return defaultValue.Value;
        if (!int.TryParse(text, out var value) || value < min || value > max) throw Error(key, $"requires an integer from {min} to {max}");
        return value;
    }
    internal IEnumerable<string> StringList(string key, bool required = false)
    {
        if (!_values.TryGetValue(key, out var node))
        {
            if (required) throw Error(key, "is required");
            return Array.Empty<string>();
        }
        if (node is not YamlSequenceNode list) throw Error(key, "requires a sequence");
        return list.Children.Select(n => Scalar(n, this, key)).ToArray();
    }
    internal IReadOnlyDictionary<string, YamlNode> Map(string key)
        => _values.TryGetValue(key, out var node) ? Child(node, _path + "." + key)._values : new Dictionary<string, YamlNode>();
    internal ConfigMapping Child(string key)
        => _values.TryGetValue(key, out var node) ? Child(node, _path + "." + key) : throw Error(key, "is required");
    internal static ConfigMapping Child(YamlNode node, string path)
        => node is YamlMappingNode mapping ? new ConfigMapping(mapping, path) : throw new ArgumentException($"{path} (line {node.Start.Line + 1}): requires a mapping.");
}
