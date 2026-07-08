// Vouchfx.Cli.Tests — no-docker unit tests for the CLI's testable seams (S07-C-01).
//
// These tests exercise ONLY the Docker-free logic:
//   • ScenarioDiscovery — finds *.e2e.yaml recursively, parses each, captures parse errors.
//   • ProviderRegistryFactory — names the 25 Core provider assemblies; the registry freezes
//     with the 25 expected step kinds.
//   • ExitCodes.FromVerdict — Pass/Inconclusive/EnvError → 0, Fail → 1.
//   • RunCommand.BuildPathArgument — `run <path>` resolves the path; bare `run` → ".".
//   • RunCommand.ScenarioName / AggregateVerdict — naming + parse-failure folding.
//
// The full `run` path (ScenarioRunner.RunSuiteAsync) starts an Aspire topology and needs
// Docker, so it is NOT invoked here.

using System.CommandLine;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ProviderRegistryFactoryTests
{
    [Fact]
    public void CoreProviderAssemblies_ReturnsTwentyFiveDistinctAssemblies()
    {
        var assemblies = ProviderRegistryFactory.CoreProviderAssemblies();

        Assert.Equal(25, assemblies.Length);
        // Twenty-five *distinct* assemblies (no accidental duplicate anchor).
        Assert.Equal(25, assemblies.Distinct().Count());
    }

    [Fact]
    public void BuildCoreRegistry_FreezesWithTheTwentyFiveCoreStepKinds()
    {
        StepKindRegistry registry = ProviderRegistryFactory.BuildCoreRegistry();

        foreach (var kind in new[]
                 {
                     "http.rest",
                     "http.soap",
                     "db-assert.postgres",
                     "db-assert.sqlserver",
                     "db-assert.mongodb",
                     "db-assert.mysql",
                     "script.csharp",
                     "mq-publish.kafka",
                     "mq-expect.kafka",
                     "mq-publish.rabbitmq",
                     "mq-expect.rabbitmq",
                     "mq-publish.nats",
                     "mq-expect.nats",
                     "mq-publish.azureservicebus",
                     "mq-expect.azureservicebus",
                     "mq-publish.redis",
                     "mq-expect.redis",
                     "webhook-listen.http",
                     "mail-expect.smtp",
                     "cache-assert.redis",
                     "cache-assert.elasticsearch",
                     "metrics-assert.prometheus",
                     "db-assert.dynamodb",
                     "storage-assert.s3",
                     "trace-expect.otlp",
                 })
        {
            Assert.True(
                registry.TryGet(kind, out var provider) && provider is not null,
                $"Expected step kind '{kind}' to be registered.");
        }
    }
}

public sealed class ExitCodesTests
{
    // The full taxonomy-aware exit-code table (S09-C-03): every verdict × every combination
    // of the two opt-in flags {failOnEnvError, failOnInconclusive}.  The invariant under test:
    //   • Fail            → TestFailure (1) ALWAYS — only Fail breaks CI by default;
    //   • EnvironmentError → 0 by default, EnvironmentError (3) only when --fail-on-env-error;
    //   • Inconclusive     → 0 by default, Inconclusive (4) only when --fail-on-inconclusive;
    //   • Pass             → Success (0) always.
    // The distinct codes 3/4 let CI tell infra breakage from a timeout from a genuine defect,
    // and side-step UsageError=2 (reserved for parse errors) so there is no collision.
    [Theory]
    // Pass — always 0, regardless of flags.
    [InlineData(Verdict.Pass, false, false, ExitCodes.Success)]
    [InlineData(Verdict.Pass, true, false, ExitCodes.Success)]
    [InlineData(Verdict.Pass, false, true, ExitCodes.Success)]
    [InlineData(Verdict.Pass, true, true, ExitCodes.Success)]
    // Fail — always 1 (TestFailure), regardless of flags.
    [InlineData(Verdict.Fail, false, false, ExitCodes.TestFailure)]
    [InlineData(Verdict.Fail, true, false, ExitCodes.TestFailure)]
    [InlineData(Verdict.Fail, false, true, ExitCodes.TestFailure)]
    [InlineData(Verdict.Fail, true, true, ExitCodes.TestFailure)]
    // EnvironmentError — 0 by default; 3 only when failOnEnvironmentError is set.
    [InlineData(Verdict.EnvironmentError, false, false, ExitCodes.Success)]
    [InlineData(Verdict.EnvironmentError, true, false, ExitCodes.EnvironmentError)]
    [InlineData(Verdict.EnvironmentError, false, true, ExitCodes.Success)]
    [InlineData(Verdict.EnvironmentError, true, true, ExitCodes.EnvironmentError)]
    // Inconclusive — 0 by default; 4 only when failOnInconclusive is set.
    [InlineData(Verdict.Inconclusive, false, false, ExitCodes.Success)]
    [InlineData(Verdict.Inconclusive, true, false, ExitCodes.Success)]
    [InlineData(Verdict.Inconclusive, false, true, ExitCodes.Inconclusive)]
    [InlineData(Verdict.Inconclusive, true, true, ExitCodes.Inconclusive)]
    public void FromVerdict_MapsPerTaxonomy(
        Verdict verdict,
        bool failOnEnvironmentError,
        bool failOnInconclusive,
        int expected)
    {
        Assert.Equal(
            expected,
            ExitCodes.FromVerdict(verdict, failOnEnvironmentError, failOnInconclusive));
    }

    [Fact]
    public void DistinctCodes_DoNotCollideWithEachOtherOrUsageError()
    {
        // 3/4 are deliberately chosen to side-step UsageError=2 (parse errors) and 0/1.
        int[] codes =
        {
            ExitCodes.Success,
            ExitCodes.TestFailure,
            ExitCodes.UsageError,
            ExitCodes.EnvironmentError,
            ExitCodes.Inconclusive,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}

public sealed class AggregateVerdictTests
{
    [Fact]
    public void NoParseFailures_LeavesSuiteVerdictUnchanged()
    {
        Assert.Equal(Verdict.Pass, RunCommand.AggregateVerdict(Verdict.Pass, parseFailureCount: 0));
    }

    [Fact]
    public void ParseFailures_ElevatePassToInconclusive()
    {
        Assert.Equal(
            Verdict.Inconclusive,
            RunCommand.AggregateVerdict(Verdict.Pass, parseFailureCount: 2));
    }

    [Fact]
    public void ParseFailures_DoNotMaskAFail()
    {
        // Fail outranks Inconclusive in the precedence ladder.
        Assert.Equal(
            Verdict.Fail,
            RunCommand.AggregateVerdict(Verdict.Fail, parseFailureCount: 1));
    }
}

public sealed class PathArgumentTests
{
    private static string ParsePath(params string[] args)
    {
        var arg = RunCommand.BuildPathArgument();
        var command = new Command("run");
        command.Add(arg);
        var result = command.Parse(args);
        Assert.Empty(result.Errors);
        return result.GetValue(arg)!;
    }

    [Fact]
    public void BareRun_DefaultsToCurrentDirectory()
    {
        Assert.Equal(".", ParsePath());
    }

    [Fact]
    public void RunWithPath_ResolvesTheSuppliedPath()
    {
        Assert.Equal("scenarios/api", ParsePath("scenarios/api"));
    }
}

public sealed class ScenarioDiscoveryTests : IDisposable
{
    private readonly string _root;
    private readonly StepKindRegistry _registry = ProviderRegistryFactory.BuildCoreRegistry();

    public ScenarioDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-cli-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    private const string MinimalValidScenario =
        "metadata:\n" +
        "  name: minimal\n" +
        "steps:\n" +
        "  - id: call-api\n" +
        "    type: http.rest\n";

    [Fact]
    public void Discover_FindsScenariosRecursively_AndSortsByPath()
    {
        var nested = Path.Combine(_root, "nested", "deep");
        Directory.CreateDirectory(nested);

        var a = Path.Combine(_root, "a.e2e.yaml");
        var b = Path.Combine(nested, "b.e2e.yaml");
        File.WriteAllText(a, MinimalValidScenario);
        File.WriteAllText(b, MinimalValidScenario);

        // A non-matching file must be ignored.
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "ignore me");
        File.WriteAllText(Path.Combine(_root, "config.yaml"), "ignore me too");

        var discovered = ScenarioDiscovery.Discover(_root, _registry);

        Assert.Equal(2, discovered.Count);
        Assert.All(discovered, d => Assert.False(d.Failed));
        Assert.All(discovered, d => Assert.NotNull(d.Ast));
        // Ordinal-sorted by absolute path → deterministic order.
        Assert.Equal(
            discovered.Select(d => d.AbsolutePath).OrderBy(p => p, StringComparer.Ordinal),
            discovered.Select(d => d.AbsolutePath));
        Assert.All(discovered, d => Assert.True(Path.IsPathFullyQualified(d.AbsolutePath)));
    }

    [Fact]
    public void Discover_CapturesParseFailures_WithoutThrowing()
    {
        var bad = Path.Combine(_root, "broken.e2e.yaml");
        // Malformed: a step with no recognised type → AstBuilder throws → captured.
        File.WriteAllText(bad, "steps:\n  - id: x\n    type: not-a-real-provider\n");

        var good = Path.Combine(_root, "good.e2e.yaml");
        File.WriteAllText(good, MinimalValidScenario);

        var discovered = ScenarioDiscovery.Discover(_root, _registry);

        Assert.Equal(2, discovered.Count);

        var brokenResult = discovered.Single(d => d.AbsolutePath == Path.GetFullPath(bad));
        Assert.True(brokenResult.Failed);
        Assert.Null(brokenResult.Ast);
        Assert.NotNull(brokenResult.ParseError);

        var goodResult = discovered.Single(d => d.AbsolutePath == Path.GetFullPath(good));
        Assert.False(goodResult.Failed);
        Assert.NotNull(goodResult.Ast);
        Assert.Null(goodResult.ParseError);
    }

    [Fact]
    public void Discover_EmptyDirectory_ReturnsEmpty()
    {
        var discovered = ScenarioDiscovery.Discover(_root, _registry);
        Assert.Empty(discovered);
    }

    [Fact]
    public void Discover_MissingRoot_Throws()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        Assert.Throws<DirectoryNotFoundException>(
            () => ScenarioDiscovery.Discover(missing, _registry));
    }

    [Fact]
    public void ScenarioName_PrefersMetadataName_FallsBackToFileStem()
    {
        var named = Path.Combine(_root, "file-stem.e2e.yaml");
        File.WriteAllText(named, MinimalValidScenario); // metadata.name = "minimal"
        var namedDiscovered = ScenarioDiscovery.ParseFile(named, _registry);
        Assert.Equal("minimal", RunCommand.ScenarioName(namedDiscovered));

        var unnamed = Path.Combine(_root, "no-name.e2e.yaml");
        File.WriteAllText(unnamed, "steps:\n  - id: call-api\n    type: http.rest\n");
        var unnamedDiscovered = ScenarioDiscovery.ParseFile(unnamed, _registry);
        Assert.Equal("no-name", RunCommand.ScenarioName(unnamedDiscovered));
    }
}
