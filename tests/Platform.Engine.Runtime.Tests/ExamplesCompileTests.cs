// ExamplesCompileTests — non-docker compile gate for EVERY examples/**/*.e2e.yaml file
// (SUT env config branch, "Also fix" work item).
//
// The published example suites under examples/ are documentation as much as they are
// fixtures — a suite author's first contact with the DSL is usually copy-pasting one of
// these files.  Nothing previously proved they stay runnable: several had silently rotted
// (nested `request:` blocks and a phantom `url:`/`statusCode:` on http.rest steps, ISO-8601
// `PTnnS` timeouts DurationParser cannot parse, `environment.services`/`dependencies`
// declared as YAML SEQUENCES where a MAPPING is required, an azureservicebus dependency's
// `queues`/`topics` nested one level too deep under a non-existent `extra:` wrapper key, and
// a `script.csharp` step using `script:` instead of the real `code:` field name).
//
// This test discovers every examples/**/*.e2e.yaml file FROM DISK (so a newly added example
// is automatically covered — nothing to remember to wire up) and drives the same front-end
// pipeline Sprint11ReferenceCompileTests proved for the reference scenario:
//   1. JSON-Schema validation (DocumentValidator.Validate) — IsValid == true.
//   2. Parse + AST (YamlDocumentParser.Parse + AstBuilder.Build) — at least one step.
//   3. Compile (ProviderPipeline.Compile) — Failure is null, Assembled is non-null and
//      non-empty.
//
// No topology is started — this is a pure front-end proof, safe for every CI run:
//   dotnet test --filter "requires!=docker&FullyQualifiedName~ExamplesCompileTests"
//
// Every Core provider referenced by any example is registered so ALL examples resolve their
// step types — see Platform.Engine.Runtime.Tests.csproj for the corresponding provider
// project references (added alongside this test).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Platform.Engine.Authoring;
using Platform.Engine.Compilation.Schema;
using Platform.Engine.Runtime;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Elasticsearch;
using Platform.Steps.CacheAssert.Redis;
using Platform.Steps.DbAssert.Mongodb;
using Platform.Steps.DbAssert.Mysql;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.DbAssert.SqlServer;
using Platform.Steps.HttpRest;
using Platform.Steps.MailExpect.Smtp;
using Platform.Steps.MetricsAssert.Prometheus;
using Platform.Steps.MqExpect.AzureServiceBus;
using Platform.Steps.MqExpect.Kafka;
using Platform.Steps.MqExpect.Nats;
using Platform.Steps.MqExpect.Rabbitmq;
using Platform.Steps.MqExpect.Redis;
using Platform.Steps.MqPublish.AzureServiceBus;
using Platform.Steps.MqPublish.Kafka;
using Platform.Steps.MqPublish.Nats;
using Platform.Steps.MqPublish.Rabbitmq;
using Platform.Steps.MqPublish.Redis;
using Platform.Steps.Script.Csharp;
using Platform.Steps.WebhookListen.Http;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker gate: every <c>examples/**/*.e2e.yaml</c> file must schema-validate, parse to
/// an AST, and compile through <see cref="ProviderPipeline"/> — so a published example can
/// never again silently rot (see the file header for the defects this gate now catches).
/// </summary>
public sealed class ExamplesCompileTests
{
    // ── Provider assemblies: every Core provider used across examples/**/*.e2e.yaml ────
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(DbAssertMysqlProvider).Assembly,
        typeof(DbAssertMongodbProvider).Assembly,
        typeof(DbAssertSqlServerProvider).Assembly,
        typeof(CacheAssertRedisProvider).Assembly,
        typeof(CacheAssertElasticsearchProvider).Assembly,
        typeof(MailExpectSmtpProvider).Assembly,
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
        typeof(MqPublishRabbitmqProvider).Assembly,
        typeof(MqExpectRabbitmqProvider).Assembly,
        typeof(MqPublishNatsProvider).Assembly,
        typeof(MqExpectNatsProvider).Assembly,
        typeof(MqPublishAzureServiceBusProvider).Assembly,
        typeof(MqExpectAzureServiceBusProvider).Assembly,
        typeof(MqPublishRedisProvider).Assembly,
        typeof(MqExpectRedisProvider).Assembly,
        typeof(MetricsAssertPrometheusProvider).Assembly,
        typeof(WebhookListenHttpProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    // ── Discover every examples/**/*.e2e.yaml file from disk ───────────────────────────
    // Mirrors Sprint11ReferenceCompileTests.ResolveRepoRoot: walk up from the test
    // assembly's output directory (bin/Debug|Release/net8.0 under
    // tests/Platform.Engine.Runtime.Tests/) to the repo root.
    private static string ResolveRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ExamplesCompileTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }

    private static string ExamplesDirectory => Path.Combine(ResolveRepoRoot(), "examples");

    /// <summary>
    /// Enumerates every <c>*.e2e.yaml</c> file under <c>examples/</c> (recursively), sorted
    /// for stable/deterministic test-case ordering.  Evaluated at test-discovery time so a
    /// newly added or removed example file is picked up automatically, with no test to update.
    /// </summary>
    public static IEnumerable<object[]> ExampleFiles()
    {
        var dir = ExamplesDirectory;
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var path in Directory
            .GetFiles(dir, "*.e2e.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return new object[] { path };
        }
    }

    /// <summary>
    /// At least one example file must be discovered — an empty <see cref="ExampleFiles"/>
    /// result would make every <see cref="Example_ParsesValidatesAndCompiles"/> case
    /// silently vanish instead of failing loudly.
    /// </summary>
    [Fact]
    public void ExampleFiles_DiscoversAtLeastOneFile()
    {
        var files = ExampleFiles().ToList();
        Assert.True(
            files.Count > 0,
            $"No examples/**/*.e2e.yaml files were discovered under '{ExamplesDirectory}'. " +
            "Either the repo-root resolution is wrong or the examples directory is empty.");
    }

    /// <summary>
    /// Every published example must schema-validate, parse to an AST with at least one
    /// step, and compile through <see cref="ProviderPipeline"/> without a container.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Example_ParsesValidatesAndCompiles(string path)
    {
        var relativePath = Path.GetRelativePath(ResolveRepoRoot(), path);

        var yaml = File.ReadAllText(path);
        Assert.False(string.IsNullOrWhiteSpace(yaml), $"{relativePath}: file must not be empty.");

        // ── 1. JSON-Schema validation (no topology) ────────────────────────────────
        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(
            validation.IsValid,
            $"{relativePath}: schema validation failed: " +
            string.Join(" | ", validation.Errors.Select(e => e.Message)));

        // ── 2. Parse + build the AST ────────────────────────────────────────────────
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);
        Assert.True(ast.Steps.Count > 0, $"{relativePath}: must declare at least one step.");

        // ── 3. Compile through the provider pipeline ────────────────────────────────
        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);
        Assert.True(
            result.Failure is null,
            $"{relativePath}: ProviderPipeline.Compile failed: {result.Failure?.Message}");
        Assert.NotNull(result.Assembled);
        Assert.False(
            string.IsNullOrWhiteSpace(result.Assembled!.CsxSource),
            $"{relativePath}: assembled CsxSource must not be empty or whitespace.");
    }
}
