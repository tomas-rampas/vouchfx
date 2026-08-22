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
using Vouchfx.Cli;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Sdk;
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

public sealed class ComputeExitCodeTests
{
    // Issue #278: `vouchfx run <dir>` where EVERY discovered/selected scenario failed to
    // parse must exit ExitCodes.Inconclusive (4) — the SAME code `validate` unconditionally
    // returns for an all-invalid set — never Success (0). RunCommand.ExecuteAsync calls
    // ScenarioRunner.RunSuiteAsync (which starts a real Aspire topology and needs Docker)
    // whenever parsedCount > 0, so the mixed-set / no-parse-failure branches below are
    // exercised directly against the extracted, Docker-free ComputeExitCode seam instead of
    // the full ExecuteAsync path — mirroring this file's own AggregateVerdictTests /
    // ExitCodesTests pattern of testing the pure decision in isolation. The all-parse-failure
    // branch IS additionally covered end-to-end (no Docker touched, since parsedCount is
    // necessarily 0 there) by RunPathRootExecuteTests / RunAllParseFailureExitCodeTests.

    [Theory]
    // Unconditional: regardless of BOTH opt-in flags, and regardless of what suiteVerdict
    // happens to be (it is always Verdict.Pass in the real flow when parsedCount == 0 — the
    // runner never ran — but the branch must dominate even if that ever changed).
    [InlineData(Verdict.Pass, false, false)]
    [InlineData(Verdict.Pass, true, false)]
    [InlineData(Verdict.Pass, false, true)]
    [InlineData(Verdict.Pass, true, true)]
    public void EntirelyParseFailures_AlwaysReturnsInconclusive_RegardlessOfFlags(
        Verdict suiteVerdict, bool failOnEnvironmentError, bool failOnInconclusive)
    {
        Assert.Equal(
            ExitCodes.Inconclusive,
            RunCommand.ComputeExitCode(
                parsedCount: 0,
                parseFailureCount: 2,
                suiteVerdict,
                failOnEnvironmentError,
                failOnInconclusive));
    }

    [Fact]
    public void EntirelyParseFailures_SingleFailure_StillReturnsInconclusive()
    {
        Assert.Equal(
            ExitCodes.Inconclusive,
            RunCommand.ComputeExitCode(
                parsedCount: 0,
                parseFailureCount: 1,
                Verdict.Pass,
                failOnEnvironmentError: false,
                failOnInconclusive: false));
    }

    // ── Mixed set (at least one scenario parsed and ran): TODAY'S behaviour, captured and
    // pinned unchanged — the new #278 branch above must NOT engage once parsedCount > 0. ──

    [Fact]
    public void MixedSet_PassingParsedScenario_DefaultFlags_ReturnsSuccess_UnchangedFromToday()
    {
        // Captures today's (arguably surprising, but explicitly out of scope for #278)
        // behaviour: a mixed set whose parsed scenario(s) all Pass still folds the parse
        // failure in as Inconclusive (AggregateVerdict), which then maps to Success by
        // default (ExitCodes.FromVerdict) because --fail-on-inconclusive was not passed.
        Assert.Equal(
            ExitCodes.Success,
            RunCommand.ComputeExitCode(
                parsedCount: 1,
                parseFailureCount: 1,
                Verdict.Pass,
                failOnEnvironmentError: false,
                failOnInconclusive: false));
    }

    [Fact]
    public void MixedSet_PassingParsedScenario_FailOnInconclusiveSet_ReturnsInconclusive()
    {
        Assert.Equal(
            ExitCodes.Inconclusive,
            RunCommand.ComputeExitCode(
                parsedCount: 1,
                parseFailureCount: 1,
                Verdict.Pass,
                failOnEnvironmentError: false,
                failOnInconclusive: true));
    }

    [Fact]
    public void MixedSet_FailingParsedScenario_AlwaysReturnsTestFailure_ParseFailureNeverMasksIt()
    {
        // Fail outranks Inconclusive in the precedence ladder (AggregateVerdictTests pins the
        // same invariant at the verdict level) — a parse-failure elsewhere in the set must
        // never downgrade a genuine product Fail into an opt-in-gated Inconclusive.
        Assert.Equal(
            ExitCodes.TestFailure,
            RunCommand.ComputeExitCode(
                parsedCount: 1,
                parseFailureCount: 1,
                Verdict.Fail,
                failOnEnvironmentError: false,
                failOnInconclusive: false));
    }

    [Theory]
    [InlineData(Verdict.Pass, ExitCodes.Success)]
    [InlineData(Verdict.Fail, ExitCodes.TestFailure)]
    public void NoParseFailures_MatchesFromVerdictDirectly_UnchangedFromToday(
        Verdict suiteVerdict, int expected)
    {
        Assert.Equal(
            expected,
            RunCommand.ComputeExitCode(
                parsedCount: 1,
                parseFailureCount: 0,
                suiteVerdict,
                failOnEnvironmentError: false,
                failOnInconclusive: false));
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

    /// <summary>
    /// A secured document with a step type no provider registers — it PARSES, then
    /// <c>AstBuilder.Build</c> refuses it. Failure class 4, the one class issue #411 closed.
    /// </summary>
    private const string SecuredButUnbuildableScenario =
        "metadata:\n"
        + "  owner: platform\n"
        + "  tags: [smoke]\n"
        + "environment:\n" +
        "  services:\n" +
        "    api:\n" +
        "      image: myorg/api:1.0\n" +
        "      security:\n" +
        "        profile: mtls\n" +
        "        endpoint: 8443\n" +
        "        clientCert: ./client.pem\n" +
        "        clientKey: ./client.key\n" +
        "steps:\n" +
        "  - id: x\n" +
        "    type: not-a-real-provider\n";

    /// <summary>
    /// The SAME declaration with an unterminated quoted scalar, so <c>YamlDocumentParser.Parse</c>
    /// itself throws and nothing binds. Failure class 3 — issue #411's residual.
    /// </summary>
    private const string SecuredAndMalformedScenario =
        "environment:\n" +
        "  services:\n" +
        "    api:\n" +
        "      image: myorg/api:1.0\n" +
        "      security:\n" +
        "        profile: mtls\n" +
        "        endpoint: 8443\n" +
        "        clientCert: ./client.pem\n" +
        "        clientKey: ./client.key\n" +
        "steps:\n" +
        "  - id: x\n" +
        "    type: \"http.rest\n";

    /// <summary>
    /// <strong>The recovery boundary, pinned where it is decided rather than only through the
    /// CLI.</strong> Issue #411's fix rests on ONE distinction — whether a bound document existed
    /// when the failure happened — and the CLI-tier rows in
    /// <c>SecurityAssuranceMatrixTests</c> would stay green if that distinction moved, as long as
    /// the exit codes came out the same for another reason. This asserts the distinction itself.
    /// </summary>
    [Fact]
    public void Discover_RecoversTheBoundDocumentOfAnUnbuildableFileOnly()
    {
        var unbuildable = Path.Combine(_root, "a-unbuildable.e2e.yaml");
        File.WriteAllText(unbuildable, SecuredButUnbuildableScenario);

        var malformed = Path.Combine(_root, "b-malformed.e2e.yaml");
        File.WriteAllText(malformed, SecuredAndMalformedScenario);

        var good = Path.Combine(_root, "c-good.e2e.yaml");
        File.WriteAllText(good, MinimalValidScenario);

        var discovered = ScenarioDiscovery.Discover(_root, _registry);

        // Class 4: the document bound, so BOTH what it declared and how it is labelled are known
        // even though it never runs — and the raw text it bound from is retained as it is for
        // every outcome, which is what lets the schema door see a `security` node that binds none
        // of the above.
        var unbuildableResult = discovered.Single(d => d.AbsolutePath == Path.GetFullPath(unbuildable));
        Assert.True(unbuildableResult.Failed);
        Assert.Null(unbuildableResult.Ast);
        Assert.NotNull(unbuildableResult.RecoveredDocument);
        Assert.NotNull(unbuildableResult.RecoveredEnvironment);
        Assert.True(SecuredTargets.Any(unbuildableResult.RecoveredEnvironment));
        Assert.Equal("platform", unbuildableResult.RecoveredMetadata?.Owner);
        Assert.Contains("smoke", unbuildableResult.RecoveredMetadata?.Tags ?? Array.Empty<string>());
        Assert.NotEmpty(unbuildableResult.YamlText);

        // Class 3: nothing bound, so nothing is recovered — and nothing pretends to be. The text is
        // still there, and is deliberately NOT read as a declaration: this class never reaches the
        // runner, so no `UnbuiltDocument` is built from it.
        var malformedResult = discovered.Single(d => d.AbsolutePath == Path.GetFullPath(malformed));
        Assert.True(malformedResult.Failed);
        Assert.Null(malformedResult.Ast);
        Assert.Null(malformedResult.RecoveredDocument);
        Assert.Null(malformedResult.RecoveredEnvironment);
        Assert.Null(malformedResult.RecoveredMetadata);

        // A document that parsed carries its environment and metadata on its Ast; these members are
        // for the failures alone and stay null, so a caller can never read them as a second source
        // of truth.
        var goodResult = discovered.Single(d => d.AbsolutePath == Path.GetFullPath(good));
        Assert.False(goodResult.Failed);
        Assert.Null(goodResult.RecoveredDocument);
        Assert.Null(goodResult.RecoveredEnvironment);
        Assert.Null(goodResult.RecoveredMetadata);
    }

    /// <summary>
    /// A malformed dependency <c>env:</c> is failure class 3 and therefore carries NO
    /// <c>RecoveredDocument</c> — so a <c>security:</c> declaration standing beside it is
    /// dropped rather than recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a RECLASSIFICATION, not merely a new error site, and that is why it is pinned
    /// here rather than left to the parser's own tests. Before <c>ParseDependencyMap</c> read
    /// <c>env:</c>, the key was swept into <c>DependencySpec.Extra</c> by <c>BuildExtraNode</c>
    /// — which accepts any scalar-KEYED child whatever the VALUE's node type — and
    /// <c>AstBuilder</c> never inspects the environment, so the document became a FULL scenario
    /// whose <c>security:</c> declaration was refused later, at the runner's schema door, with
    /// the abort recorded. It now throws inside <c>ScenarioDiscovery.ParseFile</c>'s FIRST
    /// <c>try</c>, whose catch deliberately sets no <c>RecoveredDocument</c>, and
    /// <c>RunCommand</c> builds its <c>UnbuiltDocument</c> list from
    /// <c>Where(failure =&gt; failure.RecoveredDocument is not null)</c>.
    /// </para>
    /// <para>
    /// The control file MEASURES the route the <c>env</c> file used to take rather than asserting
    /// it from memory: it is identical apart from the KEY NAME, carrying the same offending
    /// scalar value at the same place, and still parses, still builds, still exposes its
    /// security declaration, and — the assertion that makes this the ROUTE rather than only its
    /// outcome — still carries the unclaimed key in <see cref="DependencySpec.Extra"/>. The key
    /// name is the entire delta, and the key name is exactly what this change made typed.
    /// </para>
    /// <para>
    /// PINS THE BEHAVIOUR THAT EXISTS, NOT A DESIRED ONE: it matches what a malformed SERVICE
    /// <c>env:</c> has done since #159, so it is the service side's consistency rather than a new
    /// failure mode, and it sits inside issue #411's acknowledged residual. Changing the
    /// classification is that issue's work, not this one's — this test makes a future change to
    /// it visible.
    /// </para>
    /// </remarks>
    [Fact]
    public void Discover_MalformedDependencyEnv_IsClassThreeAndRecoversNothing()
    {
        var malformedEnv = Path.Combine(_root, "a-dependency-env.e2e.yaml");
        File.WriteAllText(malformedEnv, SecuredWithScalarValuedDependencyKey("env"));

        // The control: same document, same offending scalar value, a key no typed field claims.
        var untypedKey = Path.Combine(_root, "b-dependency-untyped-key.e2e.yaml");
        File.WriteAllText(untypedKey, SecuredWithScalarValuedDependencyKey("notAField"));

        var discovered = ScenarioDiscovery.Discover(_root, _registry);

        // Class 3: the parser refused it, so nothing bound and nothing is recovered. The raw
        // text is still retained (it always is) and is deliberately not read as a declaration.
        var malformedEnvResult = discovered.Single(d => d.AbsolutePath == Path.GetFullPath(malformedEnv));
        Assert.True(malformedEnvResult.Failed);
        Assert.Null(malformedEnvResult.Ast);
        Assert.Null(malformedEnvResult.RecoveredDocument);
        Assert.Null(malformedEnvResult.RecoveredEnvironment);
        Assert.Null(malformedEnvResult.RecoveredMetadata);
        Assert.NotEmpty(malformedEnvResult.YamlText);
        // The throw site, named, so a pair that started reaching some other diagnostic goes red.
        Assert.Contains("Dependency 'pg' 'env'", malformedEnvResult.ParseError!, StringComparison.Ordinal);
        Assert.Contains("must be a mapping", malformedEnvResult.ParseError!, StringComparison.Ordinal);

        // The control, and the whole evidence for the word "reclassification": an untyped key
        // holding the same value is a FULL scenario, and its security declaration is visible on
        // the Ast the runner's canonical walk consumes.
        var untypedKeyResult = discovered.Single(d => d.AbsolutePath == Path.GetFullPath(untypedKey));
        Assert.False(untypedKeyResult.Failed);
        Assert.Null(untypedKeyResult.ParseError);
        Assert.NotNull(untypedKeyResult.Ast);
        Assert.True(SecuredTargets.Any(untypedKeyResult.Ast!.Environment));

        // The ROUTE, not merely its outcome. Every assertion above would still hold if
        // BuildExtraNode had DROPPED the unclaimed key rather than sweeping it into Extra, and
        // the word "Extra" in this test's remarks — and in ParseDependencyMap's — would then be
        // unverified. Naming the bucket is what makes this a control for where 'env' used to go.
        var controlDependency = untypedKeyResult.Ast!.Environment!.Dependencies!["pg"];
        Assert.NotNull(controlDependency.Extra);
        Assert.True(
            controlDependency.Extra!.Children.Any(
                kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode { Value: "notAField" }),
            "An unclaimed scalar-keyed dependency field must be swept into DependencySpec.Extra "
                + "by BuildExtraNode — that is the route a dependency 'env:' took before it "
                + "became a typed field, and this control only measures it by naming it.");

        static string SecuredWithScalarValuedDependencyKey(string dependencyKey) =>
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            "      security:\n" +
            "        profile: mtls\n" +
            "        endpoint: 8443\n" +
            "        clientCert: ./client.pem\n" +
            "        clientKey: ./client.key\n" +
            "  dependencies:\n" +
            "    pg:\n" +
            "      type: postgres\n" +
            $"      {dependencyKey}: \"not-a-mapping\"\n" +
            "steps:\n" +
            "  - id: call-api\n" +
            "    type: http.rest\n";
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
    public void Discover_FileRoot_ReturnsSingleParsedScenario()
    {
        var file = Path.Combine(_root, "one.e2e.yaml");
        File.WriteAllText(file, MinimalValidScenario);

        var discovered = ScenarioDiscovery.Discover(file, _registry);

        var single = Assert.Single(discovered);
        Assert.False(single.Failed);
        Assert.NotNull(single.Ast);
        Assert.Equal(Path.GetFullPath(file), single.AbsolutePath);
        Assert.True(Path.IsPathFullyQualified(single.AbsolutePath));
    }

    [Fact]
    public void Discover_FileRoot_UppercaseSuffix_IsAccepted()
    {
        // Explicit naming is not discovery: an existing file the user pointed at must not
        // be rejected on suffix case (on Windows it literally is the file). The exact
        // literal name is created here, so the test also passes on case-sensitive Linux.
        var file = Path.Combine(_root, "UPPER.E2E.YAML");
        File.WriteAllText(file, MinimalValidScenario);

        var discovered = ScenarioDiscovery.Discover(file, _registry);

        var single = Assert.Single(discovered);
        Assert.False(single.Failed);
    }

    [Fact]
    public void Discover_FileRoot_NormalisesRelativeSegments()
    {
        var file = Path.Combine(_root, "one.e2e.yaml");
        File.WriteAllText(file, MinimalValidScenario);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        // Dot-segment path to the same file: sub/../one.e2e.yaml.
        var dotted = Path.Combine(_root, "sub", "..", "one.e2e.yaml");

        var discovered = ScenarioDiscovery.Discover(dotted, _registry);

        var single = Assert.Single(discovered);
        Assert.Equal(Path.GetFullPath(file), single.AbsolutePath);
    }

    [Fact]
    public void Discover_FileRoot_ParseFailure_IsCapturedNotThrown()
    {
        var bad = Path.Combine(_root, "broken.e2e.yaml");
        File.WriteAllText(bad, "steps:\n  - id: x\n    type: not-a-real-provider\n");

        var discovered = ScenarioDiscovery.Discover(bad, _registry);

        var single = Assert.Single(discovered);
        Assert.True(single.Failed);
        Assert.Null(single.Ast);
        Assert.NotNull(single.ParseError);
    }

    [Fact]
    public void Discover_FileRoot_MissingFile_ThrowsDirectoryNotFound()
    {
        var missing = Path.Combine(_root, "does-not-exist.e2e.yaml");

        var ex = Assert.Throws<DirectoryNotFoundException>(
            () => ScenarioDiscovery.Discover(missing, _registry));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        Assert.Contains("or a single *.e2e.yaml file", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("scenario.yaml")]
    [InlineData("scenario.e2e.yml")]
    public void Discover_FileRoot_WrongExtension_ThrowsScenarioDiscoveryException(string fileName)
    {
        var file = Path.Combine(_root, fileName);
        File.WriteAllText(file, MinimalValidScenario);

        var ex = Assert.Throws<ScenarioDiscoveryException>(
            () => ScenarioDiscovery.Discover(file, _registry));

        Assert.Contains(".e2e.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_DirectoryRoot_TrailingSeparator_StillDiscovers()
    {
        File.WriteAllText(Path.Combine(_root, "one.e2e.yaml"), MinimalValidScenario);

        var discovered = ScenarioDiscovery.Discover(
            _root + Path.DirectorySeparatorChar, _registry);

        Assert.Single(discovered);
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
