using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class ManagedKafkaSecurityMappingTests
{
    [Theory]
    [InlineData("kafka")]
    [InlineData("redis")]
    [InlineData("postgres")]
    public async Task UnsecuredDependency_PreservesCaseSensitiveAuthorEnvironmentNames(string type)
    {
        var environment = YamlDocumentParser.Parse($$"""
            environment:
              dependencies:
                broker:
                  type: {{type}}
                  env: { KAFKA_listeners: author-value, kafka_listeners: other-value }
            steps: []
            """).Environment;
        var mapped = EnvironmentMapper.Map(environment);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        { DisableDashboard = true, AssemblyName = "Vouchfx.Engine.Orchestration.Tests", Args = Array.Empty<string>() });
        mapped.Configure(builder);
        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>(), resource => resource.Name == "broker");
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, values);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) await callback.Callback(context);
        Assert.Equal("author-value", Assert.IsType<ReferenceExpression>(values["KAFKA_listeners"]).ValueExpression);
        Assert.Equal("other-value", Assert.IsType<ReferenceExpression>(values["kafka_listeners"]).ValueExpression);
    }

    [Fact]
    public async Task SecuredBroker_ValidMaterialAliasesAndTuningRetainAuthoredNames()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka-alias-model-").FullName;
        File.WriteAllText(Path.Combine(directory, "server.pem"), "metadata fixture, never read");
        var environment = YamlDocumentParser.Parse("""
            environment:
              dependencies:
                broker:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: tcp
                    serverArtifacts:
                      - { source: ./server.pem, target: /material/server.pem }
                  env:
                    KAFKA_ssl_keystore_type: PEM
                    KAFKA_SSL.KEYSTORE.LOCATION: /material/server.pem
                    KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_TRUSTSTORE_TYPE: PEM
                    KAFKA_listener.name.vouchfxsecure.ssl.truststore.location: /material/server.pem
                    KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: "0"
                    kafka_listeners: ignored-prefix-value
            steps: []
            """).Environment;
        var mapped = EnvironmentMapper.Map(environment, directory);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        { DisableDashboard = true, AssemblyName = "Vouchfx.Engine.Orchestration.Tests", Args = Array.Empty<string>() });
        mapped.Configure(builder);
        var resource = Assert.Single(builder.Resources.OfType<KafkaServerResource>());
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, values);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) await callback.Callback(context);
        Assert.Equal("PEM", Assert.IsType<ReferenceExpression>(values["KAFKA_ssl_keystore_type"]).ValueExpression);
        Assert.Equal("/material/server.pem", Assert.IsType<ReferenceExpression>(values["KAFKA_SSL.KEYSTORE.LOCATION"]).ValueExpression);
        Assert.Equal("PEM", Assert.IsType<ReferenceExpression>(values["KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_TRUSTSTORE_TYPE"]).ValueExpression);
        Assert.Equal("/material/server.pem", Assert.IsType<ReferenceExpression>(values["KAFKA_listener.name.vouchfxsecure.ssl.truststore.location"]).ValueExpression);
        Assert.Equal("0", Assert.IsType<ReferenceExpression>(values["KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS"]).ValueExpression);
        Assert.Equal("ignored-prefix-value", Assert.IsType<ReferenceExpression>(values["kafka_listeners"]).ValueExpression);
        Assert.Equal("none", values["KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_CLIENT_AUTH"]);
    }

    [Theory]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_TYPE", "JKS")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.truststore.type", "JKS")]
    [InlineData("KAFKA_LISTENER_NAME_VOUCHFXSECURE_SSL_KEYSTORE_LOCATION", "unpublished_material_canary")]
    [InlineData("KAFKA_listener.name.vouchfxsecure.ssl.truststore.location", "unpublished_material_canary")]
    public void SelectedListenerMaterial_InvalidRecipeRefusesDuringMapping(string key, string value)
    {
        var environment = YamlDocumentParser.Parse($$"""
            environment:
              dependencies:
                broker:
                  type: kafka
                  security: { profile: tls, endpoint: tcp }
                  env: { {{key}}: '{{value}}' }
            steps: []
            """).Environment;
        var refusal = Assert.Throws<TopologyAuthoringException>(() => EnvironmentMapper.Map(environment));
        Assert.StartsWith("environment.dependencies.broker.env:", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tcp", "tcp", 9092)]
    [InlineData("internal", "internal", 9093)]
    [InlineData("29092", "tcp", 29092)]
    [InlineData("9093", "tcp", 9093)]
    [InlineData("29093", "tcp", 29093)]
    [InlineData("8082", "tcp", 8082)]
    public async Task SecuredBroker_StagesSelectedAllocatedHostEndpointAndOverridesPlaintextDefaults(
        string selector, string endpointName, int targetPort)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-model-").FullName;
        File.WriteAllText(Path.Combine(directory, "broker.pem"), "model fixture, never read");
        var environment = YamlDocumentParser.Parse($$"""
            environment:
              dependencies:
                broker:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: "{{selector}}"
                    serverArtifacts:
                      - { source: ./broker.pem, target: /etc/kafka/secrets/broker.pem }
                  env:
                    KAFKA_SSL_KEYSTORE_TYPE: PEM
                    KAFKA_SSL_KEYSTORE_LOCATION: /etc/kafka/secrets/broker.pem
            steps: []
            """).Environment;
        var mapped = EnvironmentMapper.Map(environment, directory);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = "Vouchfx.Engine.Orchestration.Tests",
        });
        mapped.Configure(builder);
        var resource = Assert.Single(builder.Resources.OfType<KafkaServerResource>());
        foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", endpoint.Name == endpointName ? 49152 : 49153);
            endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                new AllocatedEndpoint(endpoint, "broker.container.test", endpoint.TargetPort!.Value, EndpointBindingMode.SingleAddress,
                    targetPortExpression: null!, KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
        }
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, values);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(context);

        var listeners = Assert.IsType<string>(values["KAFKA_LISTENERS"]);
        Assert.Contains($"VOUCHFXSECURE://0.0.0.0:{targetPort}", listeners, StringComparison.Ordinal);
        Assert.Contains("VOUCHFXSECURE:SSL", Assert.IsType<string>(values["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"]), StringComparison.Ordinal);
        var advertised = Assert.IsType<ReferenceExpression>(values["KAFKA_ADVERTISED_LISTENERS"]);
        Assert.Contains("VOUCHFXSECURE://", advertised.ValueExpression, StringComparison.Ordinal);
        using var addressBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rendered = await advertised.GetValueAsync(addressBudget.Token);
        Assert.Contains("VOUCHFXSECURE://localhost:49152", rendered, StringComparison.Ordinal);
        var otherEndpoint = resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name != endpointName);
        Assert.Contains($"VOUCHFXPLAIN://broker.container.test:{otherEndpoint.TargetPort}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("SSL://", advertised.ValueExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("KAFKA_SSL_KEY_PASSWORD", values.Keys);
        var resolved = await mapped.ResolveServices(null!, CancellationToken.None);
        Assert.Equal("localhost:49152", resolved["broker"]);
        Assert.DoesNotContain(resource.Annotations.OfType<HealthCheckAnnotation>(), check => check.Key == "broker_check");
        Assert.Contains(resource.Annotations.OfType<HealthCheckAnnotation>(), check => check.Key.Contains("-tcp-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingAllocatedBrokerEndpoint_HasExactResourceDiscoveryProvenance()
    {
        var environment = YamlDocumentParser.Parse("""
            environment:
              dependencies:
                broker: { type: kafka, security: { profile: tls, endpoint: internal } }
            steps: []
            """).Environment;
        var mapped = EnvironmentMapper.Map(environment);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        { DisableDashboard = true, AssemblyName = "Vouchfx.Engine.Orchestration.Tests", Args = Array.Empty<string>() });
        mapped.Configure(builder);
        var failure = await Assert.ThrowsAsync<OrchestrationException>(() => mapped.ResolveServices(null!, CancellationToken.None));
        Assert.Equal("broker", failure.Info.ResourceName);
        Assert.Equal(ResourceAcquisitionStage.Discovery, failure.AcquisitionStage);
    }

    [Theory]
    [InlineData("tcp", "internal", 9093)]
    [InlineData("internal", "tcp", 9092)]
    [InlineData("9093", "internal", 19092)]
    public async Task SchemaRegistryAndContainerSut_UseOtherPlaintextEndpointInContainerNetwork(
        string selector, string plaintextEndpointName, int plaintextPort)
    {
        var environment = YamlDocumentParser.Parse($$"""
            environment:
              dependencies:
                broker:
                  type: kafka
                  schemaRegistry: true
                  security: { profile: tls, endpoint: "{{selector}}" }
              services:
                app:
                  image: alpine:3.21
                  env: { BOOTSTRAP: '${conn:broker}' }
            steps: []
            """).Environment;
        var mapped = EnvironmentMapper.Map(environment);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        { DisableDashboard = true, AssemblyName = "Vouchfx.Engine.Orchestration.Tests", Args = Array.Empty<string>() });
        mapped.Configure(builder);
        var broker = Assert.Single(builder.Resources.OfType<KafkaServerResource>());
        var plaintext = broker.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == plaintextEndpointName);
        Assert.Equal(plaintextPort, plaintext.TargetPort);
        foreach (var endpoint in broker.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 49152);
            endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                new AllocatedEndpoint(endpoint, "broker", endpoint.TargetPort!.Value, EndpointBindingMode.SingleAddress,
                    targetPortExpression: null!, KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
        }

        using var resolutionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        foreach (var (name, key, expected) in new[]
        {
            ("broker-sr", "SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS", $"PLAINTEXT://broker:{plaintextPort}"),
            ("app", "BOOTSTRAP", $"broker:{plaintextPort}"),
        })
        {
            var resource = Assert.Single(builder.Resources.OfType<ContainerResource>(), resource => resource.Name == name);
            var values = new Dictionary<string, object>();
            var context = new EnvironmentCallbackContext(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, values);
            foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) await callback.Callback(context);
            Assert.Equal(expected, await Assert.IsType<ReferenceExpression>(values[key]).GetValueAsync(resolutionBudget.Token));
        }
    }

    [Fact]
    public void UnsecuredBroker_RetainsAspireProducerReadinessAndPorts()
    {
        var environment = YamlDocumentParser.Parse("environment: { dependencies: { broker: { type: kafka } } }\nsteps: []").Environment;
        var mapped = EnvironmentMapper.Map(environment);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        { DisableDashboard = true, AssemblyName = "Vouchfx.Engine.Orchestration.Tests", Args = Array.Empty<string>() });
        mapped.Configure(builder);
        var resource = Assert.Single(builder.Resources.OfType<KafkaServerResource>());
        Assert.Equal("broker_check", Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>()).Key);
        Assert.Equal(new int?[] { 9092, 9093 }, resource.Annotations.OfType<EndpointAnnotation>().Select(endpoint => endpoint.TargetPort).ToArray());
    }
}
