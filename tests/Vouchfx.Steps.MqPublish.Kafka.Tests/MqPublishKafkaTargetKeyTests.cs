// REQ-011 + REQ-023 (as amended 2026-08-04) — which Vars key mq-publish.kafka emits for its
// target (authenticated-infrastructure-mtls, slice E; peer review fix round three, MAJOR-2).
//
// THE DEFECT THESE PIN. `target` has accepted a declared SERVICE since REQ-011 — the shape a
// customer-supplied broker takes, because it runs its own entrypoint and configuration rather
// than being provisioned by the engine — while Emit unconditionally read VarKeys.Connection,
// i.e. conn::<name>, which the engine stages only for DEPENDENCIES. A service-targeted step
// therefore validated, probed green, and then failed on its first execution with
// "kafka bootstrap not found for key 'conn::<name>'".
//
// The fix is a COMPILE-TIME decision, from the same declared-service map this provider's own
// Validate reconciles the target against — never a runtime fallback that tries one key and then
// the other. REQ-023's rule is that the engine stages the value in the form its consumer uses and
// the provider does not transform it; trying two keys is the same class of guess.
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Kafka.Tests;

/// <summary>
/// Pins the bootstrap-key selection: <c>conn::</c> for a dependency target, <c>svc::</c> for a
/// declared-service target.
/// </summary>
public sealed class MqPublishKafkaTargetKeyTests
{
    private const string Target = "broker";

    private static MqPublishKafkaModel Model() =>
        new MqPublishKafkaProvider().Bind(
            (YamlMappingNode)new YamlStream
            {
                Documents =
                {
                    new YamlDocument(new YamlMappingNode
                    {
                        { "target", Target },
                        { "topic", "orders" },
                        { "payload", "{\"id\":1}" },
                    }),
                },
            }.Documents[0].RootNode,
            new StubBindingContext());

    /// <summary>
    /// A target that is NOT a declared service is a dependency, and stages at
    /// <c>conn::&lt;name&gt;</c> — byte-for-byte the behaviour every pre-existing suite had.
    /// </summary>
    [Fact]
    public void Emit_DependencyTarget_ReadsTheConnectionKey()
    {
        var fragment = new MqPublishKafkaProvider().Emit(
            Model(), new TargetKeyCompileContext("publish"));

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
        var fragment = new MqPublishKafkaProvider().Emit(
            Model(), new TargetKeyCompileContext("publish", Target));

        Assert.Contains($"\"svc::{Target}\"", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"conn::{Target}\"", fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compile context carrying a declared-service map, mirroring the engine's own.
    /// </summary>
    /// <remarks>
    /// Deliberately implements only the members it needs: <c>ICompileContext.DeclaredServices</c>
    /// carries a DEFAULT implementation returning the empty map, so a stand-in written before that
    /// member existed keeps compiling and keeps behaving as it did. This stub overrides it, which
    /// is what lets both branches be exercised from one type.
    /// </remarks>
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
