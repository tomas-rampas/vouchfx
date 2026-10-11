using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class ManagedKafkaSecurityPlanTests
{
    [Theory]
    [InlineData("9092")]
    [InlineData("9093")]
    [InlineData("29092")]
    [InlineData("29093")]
    [InlineData("8082")]
    [InlineData("19092")]
    public void NumericSecurePortWins_AllAuxiliaryRolesRemainDistinctAndDeterministic(string selector)
    {
        var spec = Broker(selector);
        var plan = ManagedKafkaSecurityPlan.Create("broker", spec);
        Assert.Equal(int.Parse(selector, System.Globalization.CultureInfo.InvariantCulture), plan.SecurePort);
        Assert.Equal("tcp", plan.SecureEndpointName);
        Assert.Equal("internal", plan.PlaintextEndpointName);
        Assert.Equal(5, new[] { plan.SecurePort, plan.PlaintextPort, plan.InterBrokerPort, plan.ControllerPort, plan.RestPort }.Distinct().Count());
        Assert.Equal(plan, ManagedKafkaSecurityPlan.Create("broker", spec));
    }

    [Theory]
    [InlineData("misspelled")]
    [InlineData("0")]
    [InlineData("0123")]
    [InlineData("65536")]
    public void AbsentEndpoint_IsAuthoringRefusal(string selector)
        => Assert.Throws<TopologyAuthoringException>(() => ManagedKafkaSecurityPlan.Create("broker", Broker(selector)));

    [Theory]
    [InlineData("KAFKA_LISTENERS")]
    [InlineData("KAFKA_ADVERTISED_LISTENERS")]
    [InlineData("KAFKA_CONTROLLER_QUORUM_VOTERS")]
    [InlineData("KAFKA_REST_LISTENERS")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_CLIENT_AUTH")]
    [InlineData("KAFKA_LISTENER_NAME_OTHER_SSL_CLIENT_AUTH")]
    [InlineData("KAFKA_listeners")]
    [InlineData("KAFKA.Advertised.Listeners")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_ssl_client_auth")]
    [InlineData("KAFKA_listener_name_vouchfxsecure_ssl_client_auth")]
    [InlineData("KAFKA_LISTENER.NAME.OTHER.SSL.CLIENT.AUTH")]
    [InlineData("KAFKA_listener_name_other_ssl_client_auth")]
    [InlineData("KAFKA_controller_quorum_bootstrap_servers")]
    [InlineData("KAFKA_node_id")]
    [InlineData("KAFKA_process_roles")]
    [InlineData("KAFKA_rest_listeners")]
    [InlineData("KAFKA.listeners")]
    [InlineData("KAFKAXlisteners")]
    [InlineData("KAFKA_SSL.CLIENT.AUTH")]
    public void MechanicalOverride_IsConditionalSafeRefusal(string key)
    {
        var spec = Broker("tcp") with { Env = new Dictionary<string, string> { [key] = "credential_canary_443" } };
        var refusal = Assert.Throws<TopologyAuthoringException>(() => ManagedKafkaSecurityPlan.Create("broker", spec));
        Assert.DoesNotContain("credential_canary_443", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("engine-owned", refusal.Message, StringComparison.Ordinal);
        var plain = new EnvironmentSpec(null, new Dictionary<string, DependencySpec> { ["broker"] = spec with { Security = null } }, null, null, null);
        Assert.Empty(ManagedKafkaSecurityPlan.ValidateEnvironment(plain));
    }

    [Fact]
    public void MaterialMetadata_IsValidatedWithoutReadingPathsOrClientPassphrase()
    {
        var spec = Broker("internal") with
        {
            Security = Broker("internal").Security! with
            {
                Profile = "mtls",
                ClientKeyPassword = "${secret:env/DO_NOT_READ}",
                ServerArtifacts = new[] { new SecurityServerArtifactSpec("not-on-disk-canary", "/material/server.pem") },
            },
            Env = new Dictionary<string, string>
            {
                ["KAFKA_SSL_KEYSTORE_TYPE"] = "PEM",
                ["KAFKA_SSL_KEYSTORE_LOCATION"] = "/material/server.pem",
                ["UNRELATED"] = "allowed",
            },
        };
        var plan = ManagedKafkaSecurityPlan.Create("broker", spec);
        Assert.True(plan.MutualTls);
        Assert.Equal("tcp", plan.PlaintextEndpointName);
        var wrong = spec with { Env = new Dictionary<string, string> { ["KAFKA_SSL_KEYSTORE_LOCATION"] = "private_canary_443" } };
        Assert.DoesNotContain("private_canary_443", Assert.Throws<TopologyAuthoringException>(
            () => ManagedKafkaSecurityPlan.Create("broker", wrong)).Message, StringComparison.Ordinal);
        Assert.Throws<TopologyAuthoringException>(() => ManagedKafkaSecurityPlan.Create("broker",
            spec with { Env = new Dictionary<string, string> { ["KAFKA_SSL_KEYSTORE_TYPE"] = "JKS" } }));
    }

    [Theory]
    [InlineData("KAFKA_LISTENERS")]
    [InlineData("KAFKA_listeners")]
    [InlineData("KAFKA_ssl_keystore_type")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.truststore.location")]
    public void SecretValueRefusal_PrecedesMechanicalOverrideWithoutDisclosingValue(string key)
    {
        var spec = Broker("tcp") with { Env = new Dictionary<string, string> { [key] = "${secret:env/credential_canary_443}" } };
        var environment = new EnvironmentSpec(null, new Dictionary<string, DependencySpec> { ["broker"] = spec }, null, null, null);
        var refusal = Assert.Throws<ArgumentException>(() => ManagedKafkaSecurityPlan.ValidateEnvironment(environment));
        Assert.Contains("wrong PLACE", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_canary_443", refusal.Message, StringComparison.Ordinal);
    }

    private static DependencySpec Broker(string selector) => new("kafka", null, null)
    { Security = new SecuritySpec("tls", selector, null, null, null, null) };

    [Theory]
    [InlineData("KAFKA_listeners", "listeners")]
    [InlineData("KAFKA.LISTENERS", "listeners")]
    [InlineData("KAFKAXlisteners", "listeners")]
    [InlineData("kafka_listeners", null)]
    [InlineData("Kafka_listeners", null)]
    [InlineData("KAFKA_LISTENER.NAME.VOUCHFXSECURE.SSL.CLIENT.AUTH", "listener.name.vouchfxsecure.ssl.client.auth")]
    [InlineData("KAFKA_LISTENER_NAME_X_SSL_CLIENT_AUTH", "listener.name.x_ssl.client.auth")]
    [InlineData("KAFKA_SSL__CLIENT_AUTH", "ssl_client.auth")]
    [InlineData("KAFKA_SSL___CLIENT_AUTH", "ssl-client.auth")]
    [InlineData("KAFKA_LISTENER__NAME_VOUCHFXSECURE_SSL_CLIENT_AUTH", "listener_name.vouchfxsecure.ssl.client.auth")]
    [InlineData("KAFKA_SSL.KEYSTORE.TYPE", "ssl.keystore.type")]
    [InlineData("KAFKA_ssl.truststore_location", "ssl.truststore.location")]
    public void PropertyName_MatchesPinnedRendererAsciiGrammar(string key, string? expected)
        => Assert.Equal(expected, ManagedKafkaSecurityPlan.KafkaPropertyName(key));

    [Theory]
    [InlineData("kafka_listeners")]
    [InlineData("Kafka_listeners")]
    [InlineData("KAFKA_SSL__CLIENT_AUTH")]
    [InlineData("KAFKA_SSL___CLIENT_AUTH")]
    [InlineData("KAFKA_LISTENER__NAME_VOUCHFXSECURE_SSL_CLIENT_AUTH")]
    [InlineData("KAFKA_listener_name_x_ssl_client_auth")]
    [InlineData("KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS")]
    [InlineData("KAFKA_SSL__KEYSTORE_TYPE")]
    [InlineData("KAFKA_SSL___KEYSTORE_TYPE")]
    public void DifferentEffectivePropertyAndIgnoredPrefix_RemainAuthorOwned(string key)
        => Assert.NotNull(ManagedKafkaSecurityPlan.Create("broker", Broker("tcp") with
        { Env = new Dictionary<string, string> { [key] = "author-owned" } }));

    [Theory]
    [InlineData("KAFKA_ssl_keystore_type", "JKS")]
    [InlineData("KAFKA_SSL.KEYSTORE.TYPE", "JKS")]
    [InlineData("KAFKA_ssl_truststore_type", "JKS")]
    [InlineData("KAFKA_ssl_keystore_location", "private_material_canary")]
    [InlineData("KAFKA_SSL.KEYSTORE.LOCATION", "private_material_canary")]
    [InlineData("KAFKA_ssl.truststore_location", "private_material_canary")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE", "JKS")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.keystore.type", "JKS")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_TRUSTSTORE_TYPE", "JKS")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.truststore.type", "JKS")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_LOCATION", "private_material_canary")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.truststore.location", "private_material_canary")]
    [InlineData("KAFKAXlistener.name.vouchfxsecure.ssl.keystore.type", "JKS")]
    [InlineData("KAFKA.LISTENER.NAME.VOUCHFXSECURE.SSL.TRUSTSTORE.LOCATION", "private_material_canary")]
    public void MaterialPropertyAlias_CannotBypassPureMetadataRefusal(string key, string value)
    {
        var refusal = Assert.Throws<TopologyAuthoringException>(() => ManagedKafkaSecurityPlan.Create("broker",
            Broker("tcp") with { Env = new Dictionary<string, string> { [key] = value } }));
        Assert.StartsWith("environment.dependencies.broker.env:", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KEYSTORE", false)]
    [InlineData("KEYSTORE", true)]
    [InlineData("TRUSTSTORE", false)]
    [InlineData("TRUSTSTORE", true)]
    public void CanonicalAndAliasedMaterial_AllDeclarationsAreCheckedInEitherOrder(string store, bool reverse)
    {
        var entries = new[] { new KeyValuePair<string, string>($"KAFKA_SSL_{store}_TYPE", "PEM"),
            new KeyValuePair<string, string>($"KAFKA_ssl_{store.ToLowerInvariant()}_type", "JKS") };
        var env = (reverse ? entries.Reverse() : entries).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var refusal = Assert.Throws<TopologyAuthoringException>(() => ManagedKafkaSecurityPlan.Create("broker", Broker("tcp") with { Env = env }));
        Assert.Contains("file-backed PEM", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KEYSTORE", false)]
    [InlineData("KEYSTORE", true)]
    [InlineData("TRUSTSTORE", false)]
    [InlineData("TRUSTSTORE", true)]
    public void ConflictingDeclaredMaterialLocations_RefuseRegardlessOfDeclarationOrder(string store, bool reverse)
    {
        var spec = Broker("tcp");
        var entries = new[] { new KeyValuePair<string, string>($"KAFKA_SSL_{store}_LOCATION", "/material/one.pem"),
            new KeyValuePair<string, string>($"KAFKA_ssl_{store.ToLowerInvariant()}_location", "/material/two.pem") };
        var refusal = Assert.Throws<TopologyAuthoringException>(() => ManagedKafkaSecurityPlan.Create("broker", spec with
        {
            Env = (reverse ? entries.Reverse() : entries).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Security = spec.Security! with
            {
                ServerArtifacts = new[] { new SecurityServerArtifactSpec("not-read-one", "/material/one.pem"),
                new SecurityServerArtifactSpec("not-read-two", "/material/two.pem") }
            },
        }));
        Assert.Contains("conflicting", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/material/", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingMaterialAliases_AreLegalWithoutReadingFiles()
    {
        var spec = Broker("tcp");
        Assert.NotNull(ManagedKafkaSecurityPlan.Create("broker", spec with
        {
            Env = new Dictionary<string, string>
            {
                ["KAFKA_SSL_KEYSTORE_TYPE"] = "PEM",
                ["KAFKA_ssl.keystore_type"] = "PEM",
                ["KAFKA_SSL_KEYSTORE_LOCATION"] = "/material/server.pem",
                ["KAFKA_ssl_keystore_location"] = "/material/server.pem"
            },
            Security = spec.Security! with { ServerArtifacts = new[] { new SecurityServerArtifactSpec("not-on-disk", "/material/server.pem") } },
        }));
    }

    [Theory]
    [InlineData("KEYSTORE", false)]
    [InlineData("KEYSTORE", true)]
    [InlineData("TRUSTSTORE", false)]
    [InlineData("TRUSTSTORE", true)]
    public void SelectedListenerMaterial_ChecksEveryAliasAndConflictingLocationInEitherOrder(string store, bool reverse)
    {
        var spec = Broker("tcp") with
        {
            Security = Broker("tcp").Security! with
            {
                ServerArtifacts = new[] { new SecurityServerArtifactSpec("never-read-one", "/material/one.pem"),
                    new SecurityServerArtifactSpec("never-read-two", "/material/two.pem") },
            },
        };
        var entries = new[] { new KeyValuePair<string, string>($"KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_{store}_TYPE", "PEM"),
            new KeyValuePair<string, string>($"KAFKA_listener.name.vouchfxsecure.ssl.{store.ToLowerInvariant()}.type", "JKS") };
        Dictionary<string, string> Ordered(KeyValuePair<string, string>[] values)
            => (reverse ? values.Reverse() : values).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Assert.Contains("file-backed PEM", Assert.Throws<TopologyAuthoringException>(() =>
            ManagedKafkaSecurityPlan.Create("broker", spec with { Env = Ordered(entries) })).Message, StringComparison.Ordinal);
        entries = new[] { new KeyValuePair<string, string>($"KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_{store}_LOCATION", "/material/one.pem"),
            new KeyValuePair<string, string>($"KAFKA_listener.name.vouchfxsecure.ssl.{store.ToLowerInvariant()}.location", "/material/two.pem") };
        var refusal = Assert.Throws<TopologyAuthoringException>(() =>
            ManagedKafkaSecurityPlan.Create("broker", spec with { Env = Ordered(entries) }));
        Assert.Contains("conflicting", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/material/", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedListenerMaterial_HasIndependentDeclaredLocationsAndPreservesUnrelatedSettings()
    {
        var spec = Broker("internal") with
        {
            Security = Broker("internal").Security! with
            {
                ClientKeyPassword = "${secret:env/NEVER_RESOLVE}",
                ServerArtifacts = new[] { new SecurityServerArtifactSpec("never-read-global", "/material/global.pem"),
                    new SecurityServerArtifactSpec("never-read-selected", "/material/selected.pem") },
            },
            Env = new Dictionary<string, string>
            {
                ["KAFKA_SSL_KEYSTORE_TYPE"] = "PEM",
                ["KAFKA_SSL_KEYSTORE_LOCATION"] = "/material/global.pem",
                ["KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE"] = "PEM",
                ["KAFKA_listener.name.vouchfxsecure.ssl.keystore.location"] = "/material/selected.pem",
                ["KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_LOCATION"] = "/material/selected.pem",
                ["KAFKA_LISTENER_NAME_VOUCHFXPLAIN_SSL_KEYSTORE_TYPE"] = "JKS",
                ["KAFKA_listener.name.other.ssl.keystore.location"] = "unrelated-location",
                ["kafka_listener.name.vouchfxsecure.ssl.keystore.type"] = "ignored-prefix",
                ["KAFKA_LISTENER__NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE"] = "distinct-escaped-property",
            },
        };
        Assert.NotNull(ManagedKafkaSecurityPlan.Create("broker", spec));
        var plain = new EnvironmentSpec(null, new Dictionary<string, DependencySpec>
        {
            ["broker"] = spec with
            { Security = null, Env = new Dictionary<string, string> { ["KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE"] = "JKS" } }
        }, null, null, null);
        Assert.Empty(ManagedKafkaSecurityPlan.ValidateEnvironment(plain));
        Assert.Contains("file-backed PEM", Assert.Throws<TopologyAuthoringException>(() =>
            ManagedKafkaSecurityPlan.Create("broker", spec with
            {
                Env = new Dictionary<string, string>
                {
                    ["KAFKA_SSL_KEYSTORE_TYPE"] = "JKS",
                    ["KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE"] = "PEM",
                }
            })).Message, StringComparison.Ordinal);
    }
}
