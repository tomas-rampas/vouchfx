using System.Globalization;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration;

/// <summary>Pure listener roles for a managed broker; no files, credentials or allocated addresses are read.</summary>
internal sealed record ManagedKafkaSecurityPlan(
    string SecureEndpointName, string PlaintextEndpointName, int SecurePort, int PlaintextPort,
    int InterBrokerPort, int ControllerPort, int RestPort, bool MutualTls)
{
    internal const string SecureListener = "VOUCHFXSECURE";
    internal const string PlaintextListener = "VOUCHFXPLAIN";
    private static readonly int[] s_auxiliaryPorts = { 19092, 19093, 39092, 39093, 18082 };
    private static readonly HashSet<string> s_mechanicalKeys = new(StringComparer.Ordinal)
    {
        "KAFKA_LISTENERS", "KAFKA_ADVERTISED_LISTENERS", "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP",
        "KAFKA_INTER_BROKER_LISTENER_NAME", "KAFKA_CONTROLLER_LISTENER_NAMES",
        "KAFKA_CONTROLLER_QUORUM_VOTERS", "KAFKA_CONTROLLER_QUORUM_BOOTSTRAP_SERVERS",
        "KAFKA_NODE_ID", "KAFKA_PROCESS_ROLES", "KAFKA_REST_BOOTSTRAP_SERVERS", "KAFKA_REST_LISTENERS",
        "KAFKA_SSL_CLIENT_AUTH", "KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_CLIENT_AUTH",
    };
    private static readonly Regex s_singleUnderscore = new("[^_]_[^_]", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> s_mechanicalProperties = s_mechanicalKeys
        .Select(key => KafkaPropertyName(key)!).ToHashSet(StringComparer.Ordinal);

    // The pinned Confluent renderer matches the uppercase prefix, removes its next character,
    // then converts the remaining ASCII name. Single underscores use non-overlapping matches;
    // double/triple underscores retain their distinct underscore/hyphen meanings.
    internal static string? KafkaPropertyName(string key)
    {
        if (!key.StartsWith("KAFKA", StringComparison.Ordinal) || key.Length <= 5) return null;
        var singleReplaced = s_singleUnderscore.Replace(key[6..], match => match.Value.Replace('_', '.'));
        return singleReplaced.Replace("___", "-", StringComparison.Ordinal)
            .Replace("__", "_", StringComparison.Ordinal).ToLowerInvariant();
    }

    internal static Dictionary<string, ManagedKafkaSecurityPlan> ValidateEnvironment(EnvironmentSpec? environment)
    {
        if (!SecuredTargets.Any(environment)) return new Dictionary<string, ManagedKafkaSecurityPlan>(StringComparer.Ordinal);
        EnvironmentMapper.ValidateSecurityEnvironmentValues(environment);
        foreach (var (name, spec) in environment?.Services ?? new Dictionary<string, ServiceSpec>())
        {
            if (spec.Security is not null && spec.Image is not null && ServiceEndpointNaming.ResolveSecuredPort(spec) is null)
                throw new TopologyAuthoringException($"environment.services.{name}.security.endpoint: the selector does not name a configured endpoint. Select a declared endpoint or a port in 1..65535.");
        }
        var plans = new Dictionary<string, ManagedKafkaSecurityPlan>(StringComparer.Ordinal);
        foreach (var (name, spec) in environment?.Dependencies ?? new Dictionary<string, DependencySpec>())
        {
            if (spec.Type == "kafka" && spec.Security is not null) plans[name] = Create(name, spec);
        }
        return plans;
    }

    internal static ManagedKafkaSecurityPlan Create(string name, DependencySpec spec)
    {
        var security = spec.Security!;
        var selector = security.Endpoint;
        var endpointName = selector == "internal" ? "internal" : "tcp";
        var securePort = selector switch { "tcp" => 9092, "internal" => 9093, _ => 0 };
        if (securePort == 0 && (string.IsNullOrEmpty(selector) || selector[0] == '0'
            || !int.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out securePort)
            || securePort is < 1 or > 65535))
            throw Refuse(name, "security.endpoint", "select 'tcp', 'internal', or a port in 1..65535; the named endpoint is not configured.");

        foreach (var key in spec.Env?.Keys ?? Array.Empty<string>())
        {
            var property = KafkaPropertyName(key);
            if (s_mechanicalKeys.Contains(key) || key.StartsWith("KAFKA_LISTENER_NAME_", StringComparison.Ordinal)
                && key.EndsWith("_SSL_CLIENT_AUTH", StringComparison.Ordinal)
                || property is not null && (s_mechanicalProperties.Contains(property)
                    || property.StartsWith("listener.name.", StringComparison.Ordinal)
                    && property.EndsWith(".ssl.client.auth", StringComparison.Ordinal)))
                throw Refuse(name, "env", "a listener, role, routing or client-authentication entry is engine-owned on a secured managed broker. Remove the override; use an image-form service for custom listener mechanics.");
        }
        ValidateMaterial(name, spec, "KEYSTORE");
        ValidateMaterial(name, spec, "TRUSTSTORE");

        var used = new HashSet<int> { securePort };
        int Allocate(int preferred)
        {
            if (used.Add(preferred)) return preferred;
            foreach (var candidate in s_auxiliaryPorts) if (used.Add(candidate)) return candidate;
            throw new InvalidOperationException("Managed Kafka listener role allocation exhausted its bounded candidates.");
        }
        return new ManagedKafkaSecurityPlan(endpointName, endpointName == "tcp" ? "internal" : "tcp", securePort,
            Allocate(endpointName == "tcp" ? 9093 : 9092), Allocate(29092), Allocate(29093), Allocate(8082), security.Profile == "mtls");
    }

    private static void ValidateMaterial(string name, DependencySpec spec, string store)
    {
        if (spec.Env is null) return;
        var globalPrefix = $"ssl.{store.ToLowerInvariant()}.";
        // Kafka gives selected-listener material precedence over the global fallback.
        // Check both scopes; conflicting aliases are compared within their effective property.
        foreach (var propertyPrefix in new[] { globalPrefix, $"listener.name.{SecureListener.ToLowerInvariant()}.{globalPrefix}" })
        {
            if (spec.Env.Any(pair => KafkaPropertyName(pair.Key) == propertyPrefix + "type" && pair.Value != "PEM"))
                throw Refuse(name, "env", "the managed security recipe supports file-backed PEM material. Set the SSL store type to PEM, or use an image-form service for another broker recipe.");
            var locations = spec.Env.Where(pair => KafkaPropertyName(pair.Key) == propertyPrefix + "location")
                .Select(pair => pair.Value).ToArray();
            if (locations.Any(location => !(spec.Security!.ServerArtifacts?.Any(
                artifact => string.Equals(artifact.Target, location, StringComparison.Ordinal)) ?? false)))
                throw Refuse(name, "env", "the SSL store location must exactly match a declared security.serverArtifacts target. Material values are not resolved during compilation.");
            if (locations.Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw Refuse(name, "env", "conflicting declarations for the same SSL store location are not supported. Declare one material location for each store.");
        }
    }

    private static TopologyAuthoringException Refuse(string name, string field, string reason)
        => new($"environment.dependencies.{name}.{field}: {reason}");
}
