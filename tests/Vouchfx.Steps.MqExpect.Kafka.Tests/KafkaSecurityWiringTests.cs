// REQ-015 — the mq-expect.kafka half of the Kafka mutual-TLS wiring
// (authenticated-infrastructure-mtls, slice E).
//
// The consumer is the customer's ACTUAL requirement ("a Kafka consumer over mutual TLS"), so its
// emit surface is pinned here rather than left to inherit coverage from the publisher's. The
// helper's own runtime behaviour is proven once, against a real ClientConfig, in the
// mq-publish.kafka test assembly — ProducerConfig and ConsumerConfig both derive from
// Confluent.Kafka.ClientConfig and the helper takes the base type, so there is exactly one code
// path and testing it twice would prove nothing extra. What IS specific to this provider, and is
// asserted below, is that its own emitted consumer configuration reaches that helper at all — on
// BOTH the plain and the Avro consume paths.
//
// The client-key passphrase (client-key-password, REQ-008) splits the same way: SslKeyPassword is
// declared on ClientConfig and neither subtype shadows it, so its runtime behaviour is proven in
// that same assembly, and what belongs here is the §17 property of THIS provider's own emit.
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.Kafka.Tests;

/// <summary>
/// REQ-015 tests for <c>mq-expect.kafka</c>'s emitted CSX.
/// </summary>
public sealed class KafkaSecurityWiringTests
{
    // Deliberately distinctive so the §17 assertion below cannot pass by accident against some
    // unrelated substring of the assembled script.
    private const string KeyPassphrase = "correct-horse-battery-staple-7f3a";

    private sealed class StubCompileContext : ICompileContext
    {
        internal StubCompileContext(string stepId) => StepId = stepId;

        public string StepId { get; }

        public string SuiteNamespace => "VouchfxSuite";

        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private static string EmitCsx(string target)
    {
        var node = new YamlMappingNode
        {
            { "target", target },
            { "topic", "orders" },
            { "payloadContains", "id" },
        };

        var provider = new MqExpectKafkaProvider();
        var model = provider.Bind(node, new StubBindingContext());
        var fragment = provider.Emit(model, new StubCompileContext("await-order"));
        return CsxAssembler.Assemble(new[] { ("await-order", fragment) }).CsxSource;
    }

    /// <summary>
    /// The emitted call site threads the <c>Security</c> accessor and the step's own bare
    /// <c>target</c> name — the two arguments the shared helper needs to resolve this step's
    /// declared configuration at run time.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAKafkaExpectStep_ThreadsTheSecurityAccessorAndTargetName()
    {
        var csx = EmitCsx("broker");

        Assert.Contains("MqExpectKafka_Helpers.ExpectAsync(", csx, StringComparison.Ordinal);
        Assert.Contains("Security,", csx, StringComparison.Ordinal);
        Assert.Contains("\"broker\"", csx, StringComparison.Ordinal);
    }

    /// <summary>
    /// BOTH consume paths configure security: the plain string consumer and the Avro one. A helper
    /// call on only the path a test happened to exercise would leave the other connecting in
    /// plaintext against a broker the suite declared secured.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAKafkaExpectStep_ConfiguresSecurityOnBothConsumerPaths()
    {
        var csx = EmitCsx("events");

        var configureCalls = csx
            .Split("KafkaSecurity_Helpers.ConfigureClient(security, targetName, config)", StringSplitOptions.None)
            .Length - 1;

        // One per ConsumerConfig construction in the emitted helper: the plain path and the Avro
        // path. Asserted as an exact count so a future third consume path added without the call
        // fails here rather than silently connecting unsecured.
        var consumerConfigs = csx
            .Split("new Confluent.Kafka.ConsumerConfig", StringSplitOptions.None)
            .Length - 1;

        Assert.Equal(consumerConfigs, configureCalls);
        Assert.Equal(2, configureCalls);
    }

    /// <summary>
    /// §17, applied to certificates: nothing about the declared material is baked into the
    /// compiled script — the helper reads paths from the accessor at step-execution time.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAKafkaExpectStep_CarriesOneSharedSecurityHelper()
    {
        var csx = EmitCsx("events");

        var declarations = csx.Split("static class KafkaSecurity_Helpers", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, declarations);
    }

    /// <summary>
    /// §17, applied to the client-key passphrase (client-key-password, REQ-008): the compiled
    /// script contains no passphrase. Interpolating one at emit time would bake a secret into the
    /// emitted IL and would defeat compile-once, since the script is compiled before any secret is
    /// resolved. The helper unwraps the value at step-execution time instead.
    /// </summary>
    /// <remarks>
    /// The positive half asserts the CODE that replaces it, not the identifier: the emitted source
    /// names <c>ClientKeyPassword</c> in a comment as well, so an identifier-only assertion would
    /// survive the read being deleted and the comment left behind.
    /// </remarks>
    [Fact]
    public void CompiledCsx_ForAKafkaExpectStep_BakesNoClientKeyPassphrase()
    {
        var csx = EmitCsx("events");

        Assert.DoesNotContain(KeyPassphrase, csx, StringComparison.Ordinal);
        Assert.Contains("certificates?.ClientKeyPassword?.Reveal()", csx, StringComparison.Ordinal);
    }
}
