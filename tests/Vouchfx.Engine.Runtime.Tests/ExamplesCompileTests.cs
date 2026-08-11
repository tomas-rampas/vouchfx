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
// TWO THINGS THIS GATE NOW DOES THAT A NON-DOCKER UNIT TEST NORMALLY WOULD NOT, both
// consequences of examples that declare generated files (see the fixture-material section
// below), and both stated here because neither is discoverable from a failure message:
//
//   1. IT WRITES INTO THE SOURCE TREE. Running an example's setup script produces that
//      example's declared material under examples/ — a git-ignored directory the script
//      owns. `dotnet test` is therefore not read-only with respect to the working tree.
//      Nothing outside a git-ignored fixture directory is touched.
//   2. ON WINDOWS IT REQUIRES `pwsh` ON PATH (PowerShell 7); on Linux and macOS, `bash`.
//      That is a new prerequisite of the non-docker suite, and it applies only when an
//      example ships a setup script — which today exactly one does. A missing interpreter
//      fails with a message naming it rather than with a missing-certificate error.
//
// Every Core provider referenced by any example is registered so ALL examples resolve their
// step types — see Vouchfx.Engine.Runtime.Tests.csproj for the corresponding provider
// project references (added alongside this test).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Elasticsearch;
using Vouchfx.Steps.CacheAssert.Redis;
using Vouchfx.Steps.DbAssert.Dynamodb;
using Vouchfx.Steps.DbAssert.Mongodb;
using Vouchfx.Steps.DbAssert.Mysql;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.DbAssert.SqlServer;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MailExpect.Smtp;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Vouchfx.Steps.MqExpect.AzureServiceBus;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqExpect.Nats;
using Vouchfx.Steps.MqExpect.Rabbitmq;
using Vouchfx.Steps.MqExpect.Redis;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.Steps.MqPublish.Nats;
using Vouchfx.Steps.MqPublish.Rabbitmq;
using Vouchfx.Steps.MqPublish.Redis;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.StorageAssert.S3;
using Vouchfx.Steps.TraceExpect.Otlp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

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
        typeof(DbAssertDynamodbProvider).Assembly,
        typeof(StorageAssertS3Provider).Assembly,
        typeof(TraceExpectOtlpProvider).Assembly,
        typeof(HttpSoapProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    // ── Discover every examples/**/*.e2e.yaml file from disk ───────────────────────────
    // Mirrors Sprint11ReferenceCompileTests.ResolveRepoRoot: walk up from the test
    // assembly's output directory (bin/Debug|Release/net8.0 under
    // tests/Vouchfx.Engine.Runtime.Tests/) to the repo root.
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

    // ── Fixture material: the `<example>.setup.(sh|ps1)` convention ────────────────────
    //
    // Some examples declare files that do not exist in a fresh checkout, because nothing
    // inside a suite can create them in time: `security:` paths are resolved and
    // existence-checked BEFORE any container starts, so a step runs far too late. Those
    // examples ship a setup script beside them — examples/X.setup.sh (POSIX) and
    // examples/X.setup.ps1 (Windows) for examples/X.e2e.yaml — and
    // .github/workflows/vouchfx-run-examples.yml discovers it by exactly this convention
    // before CLI-running the suite.
    //
    // This gate runs the SAME script rather than synthesising equivalent material of its
    // own. Two reasons, and the second is the one that earns the process start: a second
    // generator would be a second spelling of the file-name contract, free to drift from
    // the one the suite actually declares; and the script is a shipped artefact that
    // otherwise has no non-docker gate at all, so running it here is what stops it rotting
    // silently between full example runs.
    //
    // Once per script per test run, whichever example case reaches it first. The scripts
    // are idempotent, so the cost after the first run of a working checkout is one process
    // start that exits immediately.
    private static readonly ConcurrentDictionary<string, Lazy<string?>> s_setupScriptRuns =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Upper bound on one setup script. Generous against the work (certificate generation
    /// measures under four seconds on both scripts) and short enough that a wedged script
    /// fails this gate rather than hanging the BLOCKING CI job, which imposes no timeout of
    /// its own — neither does xunit.
    /// </summary>
    private static readonly TimeSpan s_setupScriptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <c>&lt;example&gt;.setup.(sh|ps1)</c> when one exists beside
    /// <paramref name="examplePath"/>, so the files the suite declares are present before
    /// the pipeline existence-checks them.
    /// </summary>
    /// <remarks>
    /// An example ships BOTH scripts or neither. Finding one without the other is reported
    /// as the fault it is rather than skipped: the platform-specific script alone is what a
    /// silent return would hide, and the example would then fail further down with a
    /// missing-certificate message that names nothing about the real cause. It is a live
    /// asymmetry, not a hypothetical one — CI discovers `*.setup.sh` only, so a `.sh`-only
    /// example would be green on Linux and cryptic on Windows.
    /// </remarks>
    private static void EnsureFixtureMaterial(string examplePath)
    {
        const string suffix = ".e2e.yaml";
        var stem = examplePath[..^suffix.Length] + ".setup";
        var posixScript = stem + ".sh";
        var windowsScript = stem + ".ps1";

        var posixExists = File.Exists(posixScript);
        var windowsExists = File.Exists(windowsScript);

        if (!posixExists && !windowsExists)
        {
            return;
        }

        Assert.True(
            posixExists && windowsExists,
            $"'{Path.GetFileName(examplePath)}' ships one fixture script but not the other: "
            + $"'{Path.GetFileName(posixScript)}' {(posixExists ? "exists" : "is MISSING")}, "
            + $"'{Path.GetFileName(windowsScript)}' {(windowsExists ? "exists" : "is MISSING")}. "
            + "Both are required — this gate runs the POSIX script on Linux/macOS and the "
            + "PowerShell one on Windows, and the examples workflow discovers '*.setup.sh' "
            + "only, so a half-shipped pair passes on one platform and fails on the other.");

        var script = OperatingSystem.IsWindows() ? windowsScript : posixScript;

        var failure = s_setupScriptRuns
            .GetOrAdd(script, key => new Lazy<string?>(() => RunSetupScript(key)))
            .Value;

        Assert.True(failure is null, failure);
    }

    /// <summary>
    /// Executes one setup script, returning <see langword="null"/> on success or a
    /// ready-to-assert diagnostic naming the script, its exit code and its output.
    /// </summary>
    private static string? RunSetupScript(string script) =>
        RunSetupScriptAsync(script).GetAwaiter().GetResult();

    /// <remarks>
    /// <para>
    /// <strong>Both pipes are drained CONCURRENTLY with the wait, and that is a correctness
    /// requirement rather than a tidiness one.</strong> An earlier version read stdout to
    /// completion, then stderr, then waited. That deadlocks: the parent blocks inside
    /// stdout's read until the child exits, so a child that first fills the stderr pipe
    /// buffer (4 KB on Windows) blocks forever on its own write and never exits. The
    /// large-stderr case is not exotic here — it is precisely the FAILURE path this method
    /// exists to report, since both scripts run under erroring/strict modes that emit
    /// multi-line diagnostics.
    /// </para>
    /// <para>
    /// The wait is bounded, and a timeout is reported AS a timeout — never as a pass. A
    /// stalled script would otherwise stall the blocking build job indefinitely.
    /// </para>
    /// </remarks>
    private static async Task<string?> RunSetupScriptAsync(string script)
    {
        var interpreter = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        var startInfo = new ProcessStartInfo(interpreter);

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
        }

        startInfo.ArgumentList.Add(script);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.WorkingDirectory = Path.GetDirectoryName(script)!;

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"The example fixture script '{script}' could not be started: {ex.Message}. "
                + $"'{interpreter}' must be on PATH to compile the examples that ship one.";
        }

        using (process)
        {
            // Started BEFORE the wait — see the remarks above.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(s_setupScriptTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);

                // Whatever the pipes yielded before the kill, on a short budget of its own:
                // the point is to report, not to wait a second time on a process already
                // declared stuck.
                var partial = await DrainOrGiveUpAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return $"The example fixture script '{script}' did not finish within "
                    + $"{s_setupScriptTimeout.TotalSeconds:F0}s and was killed. This is a TIMEOUT, "
                    + $"not a failure to generate — the fixture material may be absent or "
                    + $"half-written.{partial}";
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? null
                : $"The example fixture script '{script}' exited {process.ExitCode}.\n{stdout}\n{stderr}";
        }
    }

    /// <summary>Whatever the two pipes produce within a short grace, or nothing.</summary>
    private static async Task<string> DrainOrGiveUpAsync(Task<string> stdout, Task<string> stderr)
    {
        var both = Task.WhenAll(stdout, stderr);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);

        return ReferenceEquals(finished, both)
            ? $"\n{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}"
            : string.Empty;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // Exited between the check and the kill, or the platform refused it.
        }
    }

    /// <summary>
    /// An example's fixture scripts must come in pairs, and each must belong to an example.
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureFixtureMaterial"/> catches a half-shipped pair only for an example
    /// it is given. This catches the other direction — a script whose example was renamed or
    /// removed — which nothing else would: the orphan simply stops being run, and the
    /// example that should have used it fails at the security preflight naming a missing
    /// certificate rather than a missing script.
    /// </remarks>
    [Fact]
    public void FixtureScripts_ArePairedWithAnExample()
    {
        var directory = ExamplesDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var problems = new List<string>();

        foreach (var script in Directory
            .GetFiles(directory, "*.setup.*", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(script);
            if (extension is not (".sh" or ".ps1"))
            {
                problems.Add($"'{Path.GetFileName(script)}': a fixture script must be .sh or .ps1.");
                continue;
            }

            var stem = script[..^(".setup".Length + extension.Length)];

            if (!File.Exists(stem + ".e2e.yaml"))
            {
                problems.Add(
                    $"'{Path.GetFileName(script)}' has no matching "
                    + $"'{Path.GetFileName(stem)}.e2e.yaml' — an orphaned fixture script is never run.");
            }

            var sibling = extension == ".sh" ? stem + ".setup.ps1" : stem + ".setup.sh";
            if (!File.Exists(sibling))
            {
                problems.Add(
                    $"'{Path.GetFileName(script)}' has no '{Path.GetFileName(sibling)}' sibling — "
                    + "both platforms need one.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
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

        EnsureFixtureMaterial(path);

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
        // The example's OWN directory is the suite directory, exactly as `vouchfx run
        // <file>` resolves it. Previously this argument was omitted, so the pipeline fell
        // back to the process's current directory — the test assembly's bin folder — and
        // every relative path a suite declares (a `security:` artefact, a `script.csharp`
        // `file:`) was resolved somewhere no example could ever put a file. That made the
        // gate blind to the whole class of path declarations rather than lenient about it.
        var result = ProviderPipeline.Compile(
            ast, s_registry, SuiteNamespace, Path.GetDirectoryName(path));
        Assert.True(
            result.Failure is null,
            $"{relativePath}: ProviderPipeline.Compile failed: {result.Failure?.Message}");
        Assert.NotNull(result.Assembled);
        Assert.False(
            string.IsNullOrWhiteSpace(result.Assembled!.CsxSource),
            $"{relativePath}: assembled CsxSource must not be empty or whitespace.");
    }
}
