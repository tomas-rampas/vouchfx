// S11-D-01 — Reference four-technology scenario, DOCKER-GATED capstone.
//
// Loads examples/reference/reference.e2e.yaml FROM DISK (the actual published example
// file — never an inline copy, so the example and the proof can never diverge) and
// proves it runs GREEN against a real topology:
//
//   Test A (engine API): Runs the reference scenario via ScenarioRunner.RunAsync,
//     providing the seed base directory (examples/reference/) and the required secret
//     env var. Asserts Verdict.Pass, per-step verdicts in the rendered output, and
//     that the secret VALUE appears nowhere in the output.
//
//   Test B (real CLI): Invokes the actual built `vouchfx` CLI as a child process,
//     passing --events / --junit / --html artifact paths. Asserts exit code 0 (Pass),
//     all three artifact files created and non-empty, the events stream contains a
//     scenario-completed PASS record, and the secret VALUE does not appear in any
//     artifact.
//
// This test assembly carries <IsAspireHost>true</IsAspireHost> + Aspire.AppHost.Sdk
// (its .csproj), embedding the dcpclipath metadata SuiteTopology.StartAsync requires
// (R-1, CLAUDE.md §Aspire). appHostAssemblyName therefore points at THIS assembly.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~Sprint11Reference"
// Excluded from non-docker CI:  dotnet test --filter "requires!=docker"
//
// The non-docker twin (Sprint11ReferenceCompileTests, same project) proves the SAME
// file parses, validates, and compiles without any container.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Docker-gated Sprint 11 capstone: loads <c>examples/reference/reference.e2e.yaml</c>
/// from disk and proves it runs GREEN via both the engine API (<see cref="ScenarioRunner.RunAsync"/>)
/// and the real <c>vouchfx</c> CLI, end-to-end against a real topology with REST + DB +
/// Kafka + webhook, seed, secret, RETRY, and capture.
/// </summary>
public sealed class Sprint11ReferenceCapstoneTests
{
    private readonly ITestOutputHelper _output;

    public Sprint11ReferenceCapstoneTests(ITestOutputHelper output) => _output = output;

    /// <summary>Short name of THIS assembly — it carries the DCP metadata.</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>
    /// The fixed secret env-var name the reference scenario references.
    /// Tests set this env var to a sentinel value before running and remove it in a finally.
    /// </summary>
    private const string SecretEnvVarName = "VOUCHFX_REF_BEARER_TOKEN";

    /// <summary>The sentinel secret VALUE used in tests. Must never appear in any output.</summary>
    private const string SecretValue = "ref-bearer-s11-capstone-9d3f";

    // ── Provider assemblies: all six Core providers ────────────────────────────────
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
        typeof(WebhookListenHttpProvider).Assembly,
    };

    // ── Resolve paths from the test assembly location ──────────────────────────────

    /// <summary>Walks up from the test assembly output to the repo root.</summary>
    private static string ResolveRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(
            typeof(Sprint11ReferenceCapstoneTests).Assembly.Location)!;
        // Walk up: net8.0 → Release → bin → Vouchfx.Engine.Runtime.Tests → tests → repo root
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }

    private static string ReferenceYamlPath =>
        Path.Combine(ResolveRepoRoot(), "examples", "reference", "reference.e2e.yaml");

    private static string ReferenceSeedBaseDir =>
        Path.Combine(ResolveRepoRoot(), "examples", "reference");

    /// <summary>
    /// The built CLI DLL path: src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx.dll.
    /// </summary>
    private static string CliDllPath =>
        Path.Combine(
            ResolveRepoRoot(),
            "src", "Cli", "Vouchfx.Cli", "bin", "Release", "net8.0", "vouchfx.dll");

    // ── Test A: engine API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the reference scenario from disk against a real topology via
    /// <see cref="ScenarioRunner.RunAsync"/>. Asserts <see cref="Verdict.Pass"/>,
    /// per-step verdicts in the rendered output, and that the secret VALUE does not
    /// appear anywhere in the rendered output.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint11Reference_EngineApi_FourTechScenario_Pass()
    {
        Assert.True(
            File.Exists(ReferenceYamlPath),
            $"Reference YAML not found: {ReferenceYamlPath}");

        var yaml = await File.ReadAllTextAsync(ReferenceYamlPath);

        Environment.SetEnvironmentVariable(SecretEnvVarName, SecretValue);

        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        try
        {
            var verdict = await ScenarioRunner.RunAsync(
                yamlText: yaml,
                scenarioName: "reference-four-tech",
                providerAssemblies: s_providerAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                seedBaseDirectory: ReferenceSeedBaseDir,
                cancellationToken: cts.Token);

            var rendered = sw.ToString();
            _output.WriteLine("=== Terminal output ===");
            _output.WriteLine(rendered);
            _output.WriteLine($"Verdict: {verdict}");

            // ── 1. The scenario passes end-to-end ──────────────────────────────────
            Assert.Equal(Verdict.Pass, verdict);

            // ── 2. Per-step verdicts rendered: all six step ids + PASS ─────────────
            string[] stepIds =
            {
                "rest-get-whoami",
                "db-insert-row",
                "db-assert-rows",
                "kafka-publish-order",
                "kafka-expect-order",
                "webhook-trigger",
                "webhook-await",
            };
            foreach (var stepId in stepIds)
            {
                Assert.Contains(stepId, rendered, StringComparison.Ordinal);
            }
            Assert.Contains("PASS", rendered, StringComparison.OrdinalIgnoreCase);

            // ── 3. No secret value in the rendered output (§17) ────────────────────
            Assert.DoesNotContain(SecretValue, rendered, StringComparison.Ordinal);

            // ── 4. No internal engine key prefixes in the rendered output ──────────
            Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(VarKeys.CaptureStatusPrefix, rendered, StringComparison.Ordinal);

            _output.WriteLine(
                "Reference four-tech proof: REST + DB + Kafka + webhook composed → Pass. " +
                "Secret value not leaked.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVarName, null);
        }
    }

    // ── Test B: real CLI ───────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the built <c>vouchfx</c> CLI as a real child process running the reference
    /// scenario from disk, with <c>--events</c> / <c>--junit</c> / <c>--html</c> artifact
    /// paths.  Asserts exit code 0 (Pass), the three artifact files are created and
    /// non-empty, the events stream contains a scenario-completed PASS and no secret value,
    /// and the HTML and JUnit artifacts are non-empty.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint11Reference_RealCli_FourTechScenario_ExitCode0_ArtifactsCreated()
    {
        Assert.True(
            File.Exists(ReferenceYamlPath),
            $"Reference YAML not found: {ReferenceYamlPath}");

        Assert.True(
            File.Exists(CliDllPath),
            $"CLI DLL not found: {CliDllPath}\n" +
            "Build the solution in Release mode first: dotnet build vouchfx.sln -c Release");

        var tmpDir = Path.Combine(
            Path.GetTempPath(), "vouchfx-s11-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var eventsPath = Path.Combine(tmpDir, "events.jsonl");
        var junitPath = Path.Combine(tmpDir, "results.xml");
        var htmlPath = Path.Combine(tmpDir, "results.html");

        try
        {
            // ── Invoke the real CLI as a child process ────────────────────────────
            // The CLI's `run <path>` takes a DIRECTORY (it globs for *.e2e.yaml within it),
            // not a direct file path.  Pass the examples/reference/ directory so the runner
            // discovers reference.e2e.yaml automatically.
            //
            // The CLI passes seedBaseDirectory: null to RunSuiteAsync, which means seed file
            // paths in the YAML resolve relative to the CLI process's WORKING DIRECTORY.
            // Set the working directory to examples/reference/ so that "fixtures/seed.sql"
            // in the YAML resolves to the correct path alongside the scenario file.
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments =
                    $"\"{CliDllPath}\" run \"{ReferenceSeedBaseDir}\"" +
                    $" --events \"{eventsPath}\"" +
                    $" --junit \"{junitPath}\"" +
                    $" --html \"{htmlPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Working directory = examples/reference/ so seed paths ("fixtures/seed.sql")
                // resolve relative to the scenario file's location.
                WorkingDirectory = ReferenceSeedBaseDir,
            };

            // Pass the secret env var into the child process.
            psi.Environment[SecretEnvVarName] = SecretValue;
            // Opt telemetry out (no consent file in CI).
            psi.Environment["VOUCHFX_NO_TELEMETRY"] = "1";

            using var proc = new Process { StartInfo = psi };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            proc.Start();

            // Capture stdout + stderr concurrently to avoid deadlock on full buffer.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            await proc.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            _output.WriteLine("=== CLI stdout ===");
            _output.WriteLine(stdout);
            _output.WriteLine("=== CLI stderr ===");
            _output.WriteLine(stderr);
            _output.WriteLine($"Exit code: {proc.ExitCode}");

            // ── 1. Exit code 0 = Pass (§12.1: only Fail exits 1) ─────────────────
            Assert.Equal(0, proc.ExitCode);

            // ── 2. All three artifact files created and non-empty ─────────────────
            Assert.True(File.Exists(eventsPath), $"Events file not created: {eventsPath}");
            Assert.True(File.Exists(junitPath), $"JUnit file not created: {junitPath}");
            Assert.True(File.Exists(htmlPath), $"HTML file not created: {htmlPath}");

            var eventsContent = await File.ReadAllTextAsync(eventsPath);
            var junitContent = await File.ReadAllTextAsync(junitPath);
            var htmlContent = await File.ReadAllTextAsync(htmlPath);

            Assert.False(string.IsNullOrWhiteSpace(eventsContent),
                "Events file must not be empty.");
            Assert.False(string.IsNullOrWhiteSpace(junitContent),
                "JUnit file must not be empty.");
            Assert.False(string.IsNullOrWhiteSpace(htmlContent),
                "HTML file must not be empty.");

            // ── 3. Events stream contains a scenario-completed PASS ───────────────
            // The v1 wire format (VerdictJsonConverter) serialises Verdict.Pass as "PASS".
            // The ScenarioCompletedEvent record emits both the "scenario-completed" type
            // token and a "verdict":"PASS" field on the same JSON-Lines record.
            // Assert both tokens appear in the events file — robust against field
            // reordering, avoids substring false-positives (e.g. "Passphrase").
            Assert.Contains("scenario-completed", eventsContent, StringComparison.Ordinal);
            Assert.Contains("\"verdict\":\"PASS\"", eventsContent, StringComparison.Ordinal);
            // The scenario name must appear in the stream.
            Assert.Contains("reference-four-tech", eventsContent, StringComparison.Ordinal);

            // ── 4. No secret value in any artifact ────────────────────────────────
            Assert.DoesNotContain(SecretValue, eventsContent, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, junitContent, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, htmlContent, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, stdout, StringComparison.Ordinal);

            // ── 5. JUnit artifact contains a test-suite / test-case element ───────
            Assert.Contains("<testsuite", junitContent, StringComparison.OrdinalIgnoreCase);

            // ── 6. HTML artifact is a proper HTML document ────────────────────────
            Assert.Contains("<html", htmlContent, StringComparison.OrdinalIgnoreCase);

            _output.WriteLine(
                "CLI proof: vouchfx run reference.e2e.yaml → exit 0 (Pass); " +
                "events + JUnit + HTML created; no secret leak.");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch (IOException) { }
        }
    }
}
