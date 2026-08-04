// REQ-011 + REQ-023 (as amended 2026-08-04) — which Vars key mq-expect.kafka emits for its
// target (authenticated-infrastructure-mtls, slice E; peer review fix round three, MAJOR-2).
//
// The twin of MqPublishKafkaTargetKeyTests, and it is a separate assertion rather than a shared
// one because the two providers emit independently: a fix applied to one and not the other leaves
// a suite that can publish to a service-declared broker and then cannot read back from it. See
// that file's header for the defect and for why the decision is made at compile time.
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.Kafka.Tests;

/// <summary>
/// Pins the bootstrap-key selection: <c>conn::</c> for a dependency target, <c>svc::</c> for a
/// declared-service target.
/// </summary>
public sealed class MqExpectKafkaTargetKeyTests
{
    private const string Target = "broker";

    private static MqExpectKafkaModel Model() =>
        new MqExpectKafkaProvider().Bind(
            new YamlMappingNode
            {
                { "target", Target },
                { "topic", "orders" },
                { "payloadContains", "id" },
            },
            new StubBindingContext());

    /// <summary>
    /// A target that is NOT a declared service is a dependency, and stages at
    /// <c>conn::&lt;name&gt;</c> — byte-for-byte the behaviour every pre-existing suite had.
    /// </summary>
    [Fact]
    public void Emit_DependencyTarget_ReadsTheConnectionKey()
    {
        var fragment = new MqExpectKafkaProvider().Emit(
            Model(), new TargetKeyCompileContext("await-order"));

        Assert.Contains($"\"conn::{Target}\"", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"svc::{Target}\"", fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// A target naming a DECLARED SERVICE stages at <c>svc::&lt;name&gt;</c>, which is where the
    /// engine actually puts a service's endpoint — the half of MAJOR-2 that lives in the provider.
    /// </summary>
    [Fact]
    public void Emit_ServiceTarget_ReadsTheServiceKey()
    {
        var fragment = new MqExpectKafkaProvider().Emit(
            Model(), new TargetKeyCompileContext("await-order", Target));

        Assert.Contains($"\"svc::{Target}\"", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"conn::{Target}\"", fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compile context carrying a declared-service map, mirroring the engine's own.
    /// </summary>
    private sealed class TargetKeyCompileContext : ICompileContext
    {
        private static readonly IReadOnlyDictionary<string, DeclaredServiceInfo> None =
            new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);

        internal TargetKeyCompileContext(string stepId, params string[] serviceNames)
        {
            StepId = stepId;
            DeclaredServices = serviceNames.Length == 0
                ? None
                : serviceNames.ToDictionary(
                    name => name,
                    name => new DeclaredServiceInfo(Array.Empty<string>()),
                    StringComparer.Ordinal);
        }

        public string StepId { get; }

        public string SuiteNamespace => "VouchfxSuite";

        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; }

        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }
}
