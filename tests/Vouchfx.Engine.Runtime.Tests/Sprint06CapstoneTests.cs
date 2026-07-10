// S06 Capstone — Sprint 6 integration proof (DOCKER-GATED).
//
// Proves the two Sprint 6 Kafka providers compose end-to-end against a REAL kafka +
// Confluent Schema Registry topology, with the Avro path and engine-owned RETRY polling:
//
//   Test 1 — happy path (Avro publish → RETRY expect → Pass):
//     1. mq-publish.kafka (IMMEDIATE, avro): builds an Avro Order GenericRecord from the
//        inline schema + record map and produces it to orders.created through the live
//        schema registry — which AUTO-REGISTERS the schema under orders.created-value
//        (the "registry-validated" acceptance criterion).
//     2. mq-expect.kafka (RETRY, avro): the engine wraps the idempotent single poll in
//        RetryRunner.PollAsync; each attempt consumes the topic from the start, Avro-
//        decodes each message to JSON, and matches $.id == {orderId}.  When the published
//        record is decoded and matched → Pass.
//     => Verdict.Pass.  Proves: Avro publish → schema auto-registered → RETRY-expect polls
//        → decodes the Avro message → matches → Pass.
//
//   Test 2 — never-satisfied (Avro publish → RETRY expect that never matches → Inconclusive):
//     Same topology; the publish exercises the broker/registry, but the RETRY expect
//     selects $.id == {missingId} (a value that is NEVER published) with a SHORT timeout.
//     Each attempt is a non-match (Fail); the RETRY runner exhausts the window and the
//     sustained Fail is converted to Inconclusive on timeout (§12.1) — NOT Fail.
//     => Verdict.Inconclusive AND != Verdict.Fail.  Proves a never-satisfied RETRY does
//        NOT break CI (§12.1: only Fail breaks CI by default).
//
// This test assembly carries <IsAspireHost>true</IsAspireHost> + Aspire.AppHost.Sdk (its
// .csproj), embedding the dcpclipath metadata SuiteTopology.StartAsync requires (R-1,
// CLAUDE.md §Aspire).  appHostAssemblyName therefore points at THIS assembly.  The
// Confluent.Kafka / Confluent.SchemaRegistry(.Serdes.Avro) / Apache.Avro client DLLs are
// referenced directly by this project so they deploy to the output dir for the providers'
// Assembly.Location runtime resolution.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~Sprint06"
// Excluded from non-docker CI:  dotnet test --filter "requires!=docker"
//
// The non-docker twin (Sprint06CapstoneCompileTests, same project) proves the SAME two
// scenarios COMPILE end-to-end (parse → validate → AST → ProviderPipeline.Compile) without
// any container — including the RETRY wrapping and both Avro Kafka provider contributions.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Docker-gated Sprint 6 capstone: wires the Avro <c>mq-publish.kafka</c> and
/// RETRY <c>mq-expect.kafka</c> providers against a real kafka + schema-registry
/// topology to prove (1) Avro publish → schema auto-registration → RETRY-decode →
/// Pass, and (2) a never-satisfied RETRY resolves to Inconclusive (not Fail), §12.1.
/// </summary>
public sealed class Sprint06CapstoneTests
{
    private readonly ITestOutputHelper _output;

    public Sprint06CapstoneTests(ITestOutputHelper output) => _output = output;

    /// <summary>Short name of THIS assembly — it carries the DCP metadata.</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>Step ids, asserted to appear in the rendered per-step output.</summary>
    private const string PublishStepId = "publish-order";
    private const string ExpectStepId = "expect-order";
    private const string ExpectMissingStepId = "expect-missing";

    // ── Provider assemblies: the two Sprint 6 Kafka providers, discovered reflectively ─
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
    };

    // ── Capstone YAMLs (byte-identical to the compile twin Sprint06CapstoneCompileTests) ─

    /// <summary>
    /// Happy-path scenario: Avro publish of an Order record (id = <c>{orderId}</c>) to
    /// <c>orders.created</c> through the live schema registry, then a RETRY Avro expect
    /// that polls until it decodes a message whose <c>$.id</c> matches <c>{orderId}</c>.
    /// </summary>
    private const string HappyPathYaml = Sprint06CapstoneCompileTests.HappyPathYaml;

    /// <summary>
    /// Never-satisfied scenario: the same Avro publish (so the topic / broker / registry
    /// are exercised), then a RETRY Avro expect with a SHORT timeout whose <c>$.id</c>
    /// match selects <c>{missingId}</c> — a value that is never published.
    /// </summary>
    private const string NeverSatisfiedYaml = Sprint06CapstoneCompileTests.NeverSatisfiedYaml;

    // ── Test 1: happy path → Pass ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs the happy-path capstone scenario against a real kafka + schema-registry
    /// topology and asserts <see cref="Verdict.Pass"/>: the Avro publish auto-registers
    /// the schema, and the RETRY-wrapped Avro expect decodes and matches the published
    /// record.  Also asserts both step ids and PASS render, and no internal value leaks.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint06Capstone_AvroPublishThenRetryExpect_Pass()
    {
        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: HappyPathYaml,
            scenarioName: "sprint-06-capstone",
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine("=== Terminal output ===");
        _output.WriteLine(rendered);
        _output.WriteLine($"Verdict: {verdict}");

        // ── 1. The scenario passes end-to-end ─────────────────────────────────────
        // Pass proves: Avro publish → schema auto-registered with the live registry →
        // RETRY-expect polls → decodes the Avro message → $.id matches → Pass.
        Assert.Equal(Verdict.Pass, verdict);

        // ── 2. Per-step verdicts rendered: both step ids + PASS ───────────────────
        Assert.Contains(PublishStepId, rendered, StringComparison.Ordinal);
        Assert.Contains(ExpectStepId, rendered, StringComparison.Ordinal);
        Assert.Contains("PASS", rendered, StringComparison.OrdinalIgnoreCase);

        // ── 3. No value leak in the terminal output ───────────────────────────────
        // Internal engine key prefixes (conn::, __capture_status::) and raw JSON "value"
        // properties must never surface in user-visible output.
        Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(VarKeys.CaptureStatusPrefix, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\"", rendered, StringComparison.Ordinal);

        _output.WriteLine("No-value-leak assertion: PASS.");
    }

    // ── Test 2: never-satisfied RETRY → Inconclusive (NOT Fail) ───────────────────────

    /// <summary>
    /// Runs the never-satisfied capstone scenario: the Avro publish succeeds, but the
    /// RETRY Avro expect selects a <c>$.id</c> value that is never published, with a short
    /// timeout.  Asserts <see cref="Verdict.Inconclusive"/> AND that the verdict is NOT
    /// <see cref="Verdict.Fail"/> — the §12.1 proof that a never-satisfied RETRY does not
    /// break CI.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint06Capstone_NeverSatisfiedRetry_Inconclusive_NotFail()
    {
        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: NeverSatisfiedYaml,
            scenarioName: "sprint-06-capstone-never",
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine("=== Terminal output ===");
        _output.WriteLine(rendered);
        _output.WriteLine($"Verdict: {verdict}");

        // ── 1. The RETRY window elapses with only non-matches → Inconclusive ──────
        // The engine-owned RetryRunner retries the idempotent expect poll; each attempt
        // is a non-match (Fail), and a sustained Fail is converted to Inconclusive on
        // timeout (§12.1).  The publish step itself passed (the broker was exercised), so
        // the scenario's terminal verdict is the expect step's Inconclusive.
        Assert.Equal(Verdict.Inconclusive, verdict);

        // ── 2. §12.1 proof: a never-satisfied RETRY does NOT break CI ─────────────
        // Only Fail breaks CI by default; conflating a never-satisfied RETRY with a defect
        // would destroy trust in the tool.  This assertion is the explicit guard.
        Assert.NotEqual(Verdict.Fail, verdict);

        // ── 3. The expect step is rendered (it ran and exhausted its RETRY window) ─
        Assert.Contains(ExpectMissingStepId, rendered, StringComparison.Ordinal);
        // The terminal renderer surfaces the Inconclusive disposition for the expect step.
        Assert.Contains("INCONCLUSIVE", rendered, StringComparison.OrdinalIgnoreCase);

        // ── 4. No value leak in the terminal output ───────────────────────────────
        Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(VarKeys.CaptureStatusPrefix, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\"", rendered, StringComparison.Ordinal);

        _output.WriteLine(
            "Never-satisfied RETRY proof: Inconclusive (not Fail) → CI is not broken (§12.1).");
    }
}
