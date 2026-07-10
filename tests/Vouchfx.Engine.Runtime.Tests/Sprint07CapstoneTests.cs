// S07 Capstone — Sprint 7 integration proof (DOCKER-GATED).
//
// Proves the Sprint 7 feature set composes end-to-end against a REAL topology + the
// host-owned ephemeral webhook listener, with the engine-owned RETRY polling and the
// relational-diff renderer:
//
//   Test 1 — webhook callback captured (script-simulated SUT → listener → RETRY → Pass):
//     A two-step scenario over a real topology that stands up the host listener:
//       1. trigger-callback (script.csharp): reads the host listener's callback URL from
//          Vars["cb"] (the plain-key staging the runner sets BEFORE step 1), threads a
//          {ref} order id forward into Vars, and POSTs to <cb>callbacks/<ref> — simulating
//          the system-under-test calling back into the listener.
//       2. await-callback (webhook-listen.http, RETRY): the engine wraps the idempotent
//          single scan in RetryRunner.PollAsync; each attempt scans the listener's capture
//          buffer for a POST to /callbacks/<ref>.  When the callback is captured → Pass.
//     => Verdict.Pass.  Proves: host listener stood up → URL staged in Vars → a step caused
//        an inbound webhook → webhook-listen.http RETRY captured it → asserted → Pass.
//
//   Test 2 — failing db-assert renders a relational diff (wrong-expected → Fail + diff):
//     A real postgres dependency is seeded with one orders row whose status is PENDING; a
//     db-assert.postgres step asserts status == SHIPPED, so the column mismatch Fails and
//     the terminal renderer draws the provider's IStepDiffRenderer relational table under
//     the failed step (column | expected | actual, with SHIPPED vs PENDING).
//     => Verdict.Fail AND the rendered output contains the diff table.  Proves the S07
//        IStepDiffRenderer surface live: a failed db-assert.postgres shows a relational
//        expected-vs-observed diff (Sprint 7 exit criterion).
//
// ── Callback approach (HONEST disclosure) ───────────────────────────────────────────
// The realistic production flow is: an http.rest POST hands the callback URL {cb} to a
// containerised SUT, the SUT calls back over host.docker.internal, then webhook-listen.http
// captures it.  No generic off-the-shelf SUT image reliably echoes an arbitrary callback
// URL back to the host, so Test 1 SIMULATES the SUT callback with a script.csharp step that
// POSTs to the listener URL read from Vars["cb"].  This still proves the full
// listener + capture + RETRY + assertion path end-to-end through a REAL topology (the
// listener is the host-owned Kestrel resource the runner starts, NOT an in-test stub).  The
// remaining Docker-ONLY validation a real containerised-SUT callback would add is the
// host.docker.internal network hop from a container back to the host listener — the listener
// binds 0.0.0.0:0 (S07-F-01a) for exactly that, and the orchestration WebhookListenerTests
// already cover the bind/capture/token-strip mechanics over the loopback.  The XPath-capture-
// threads-forward surface is proven live in-process by HttpRestXPathCaptureTests and at the
// compile level by Sprint07CapstoneCompileTests (the webhook YAML there carries the real
// `capture: { ref: { xpath: "//order/id" } }`); here Test 1 threads {ref} via script.csharp
// so the run is self-contained against any topology.
//
// This test assembly carries <IsAspireHost>true</IsAspireHost> + Aspire.AppHost.Sdk (its
// .csproj), embedding the dcpclipath metadata SuiteTopology.StartAsync requires (R-1,
// CLAUDE.md §Aspire).  appHostAssemblyName therefore points at THIS assembly.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~Sprint07"
// Excluded from non-docker CI:  dotnet test --filter "requires!=docker"
//
// The non-docker twin (Sprint07CapstoneCompileTests, same project) proves the SAME two
// scenarios COMPILE end-to-end (parse → validate → AST → ProviderPipeline.Compile) without
// any container — including the RETRY wrapping, the webhook-listen helper, the http.rest
// XPath capture + forward {ref} substitution, the db-assert failing expectation, and the
// IStepDiffRenderer relational render.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Docker-gated Sprint 7 capstone: wires <c>script.csharp</c> + <c>webhook-listen.http</c>
/// (RETRY) against a real topology with the host-owned ephemeral webhook listener to prove
/// (1) a step causes an inbound webhook that the RETRY listener captures → Pass, and wires
/// <c>db-assert.postgres</c> against a real seeded postgres to prove (2) a failed assertion
/// renders a relational expected-vs-observed diff (the S07 exit criteria, minus parallelism).
/// </summary>
public sealed class Sprint07CapstoneTests
{
    private readonly ITestOutputHelper _output;

    public Sprint07CapstoneTests(ITestOutputHelper output) => _output = output;

    /// <summary>Short name of THIS assembly — it carries the DCP metadata.</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>Step ids, asserted to appear in the rendered per-step output.</summary>
    private const string TriggerStepId = "trigger-callback";
    private const string AwaitStepId = "await-callback";
    private const string AssertStepId = "assert-status";

    // ── Provider assemblies: the Sprint 7 Core providers, discovered reflectively ──────
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(WebhookListenHttpProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    // ── Capstone YAMLs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The WEBHOOK-CALLBACK capstone scenario (Pass).  The first step (script.csharp)
    /// reads the host listener's callback URL from <c>Vars["cb"]</c>, threads a
    /// <c>ref</c> order id forward, and POSTs to <c>&lt;cb&gt;callbacks/&lt;ref&gt;</c> —
    /// simulating the SUT calling back.  The second step (webhook-listen.http, RETRY)
    /// captures the inbound POST whose path is <c>/callbacks/{ref}</c>.
    /// </summary>
    /// <remarks>
    /// No <c>environment</c> block is declared: the only resource this scenario needs is the
    /// host-owned webhook listener, which the <c>webhook-listen.http</c> step contributes as
    /// a HOST resource (the runner stands it up before step 1 and stages its URL at
    /// <c>Vars["cb"]</c> / <c>Vars["svc::cb"]</c>).  A minimal topology is still started.
    /// </remarks>
    private const string WebhookYaml = """
        metadata:
          name: sprint-07-capstone-webhook
          owner: vouchfx-core
          tags: [s7-capstone, webhook]
          description: A script.csharp step POSTs to the host listener callback URL; a webhook-listen.http RETRY step captures it. Proves the host listener + capture + RETRY end-to-end.

        steps:
          - id: trigger-callback
            type: script.csharp
            code: |
              // Thread an order ref forward (the value a real XPath/JSONPath capture would
              // have extracted from the SUT response) and call back into the host listener.
              var orderRef = "ord-7F3A";
              Vars["ref"] = orderRef;
              var cb = (string)Vars["cb"];   // host-listener URL staged by the runner (ends with '/').
              var client = new System.Net.Http.HttpClient();
              try {
                var content = new System.Net.Http.StringContent(
                  "{\"orderRef\":\"" + orderRef + "\"}",
                  System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(cb + "callbacks/" + orderRef, content);
                if ((int)resp.StatusCode >= 400)
                  throw new System.Exception("Callback POST failed: " + (int)resp.StatusCode);
              } finally { client.Dispose(); }

          - id: await-callback
            type: webhook-listen.http
            listener: cb
            verifyMode: RETRY
            timeout: 30s
            match:
              method: POST
              path: "/callbacks/{ref}"
        """;

    /// <summary>
    /// The RELATIONAL-DIFF capstone scenario (Fail + diff).  A <c>db-assert.postgres</c>
    /// step asserts <c>status == SHIPPED</c> against a seeded <c>orders</c> row whose status
    /// is <c>PENDING</c>, so the column mismatch Fails and the renderer draws the provider's
    /// relational diff under the failed step.
    /// </summary>
    private const string DiffYaml = """
        metadata:
          name: sprint-07-capstone-diff
          owner: vouchfx-core
          tags: [s7-capstone, diff]
          description: A failing db-assert.postgres renders a relational expected-vs-observed diff. Proves the S7 IStepDiffRenderer surface live.

        environment:
          dependencies:
            shop:
              type: postgres
          seed:
            shop:
              sql: [ "fixtures/orders.sql" ]

        steps:
          - id: assert-status
            type: db-assert.postgres
            target: shop
            query: >-
              SELECT status FROM orders WHERE id = 1
            verifyMode: IMMEDIATE
            expect:
              rowCount: 1
              row:
                status: SHIPPED
        """;

    // ── Seed SQL for the diff scenario: one orders row with status PENDING ─────────────
    // The db-assert asserts status == SHIPPED, so the seeded PENDING value drives the
    // column-mismatch Fail whose observation the IStepDiffRenderer renders.
    private const string OrdersSql =
        "CREATE TABLE orders (id INT PRIMARY KEY, status TEXT NOT NULL);\n" +
        "INSERT INTO orders (id, status) VALUES (1, 'PENDING');\n";

    // ── Test 1: host listener + webhook-listen.http RETRY captures the callback → Pass ─

    /// <summary>
    /// Runs the webhook-callback capstone scenario against a real topology: the runner
    /// stands up the host-owned listener and stages its URL in <c>Vars</c>; a script.csharp
    /// step POSTs to it; and the RETRY <c>webhook-listen.http</c> step captures the inbound
    /// request and asserts on it.  Asserts <see cref="Verdict.Pass"/> and that both step ids
    /// render with a PASS verdict, with no internal value leak.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint07Capstone_WebhookCallbackCapturedByRetryListener_Pass()
    {
        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: WebhookYaml,
            scenarioName: "sprint-07-capstone-webhook",
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine("=== Terminal output ===");
        _output.WriteLine(rendered);
        _output.WriteLine($"Verdict: {verdict}");

        // ── 1. The scenario passes end-to-end ─────────────────────────────────────
        // Pass proves: host listener stood up → URL staged in Vars → script.csharp caused
        // an inbound webhook → webhook-listen.http RETRY scanned the capture buffer →
        // matched the POST to /callbacks/<ref> → Pass.
        Assert.Equal(Verdict.Pass, verdict);

        // ── 2. Per-step verdicts rendered: both step ids + PASS ───────────────────
        Assert.Contains(TriggerStepId, rendered, StringComparison.Ordinal);
        Assert.Contains(AwaitStepId, rendered, StringComparison.Ordinal);
        Assert.Contains("PASS", rendered, StringComparison.OrdinalIgnoreCase);

        // ── 3. No value leak in the terminal output ───────────────────────────────
        // Internal engine key prefixes (conn::, __capture_status::) and raw JSON "value"
        // properties must never surface in user-visible output.
        Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(VarKeys.CaptureStatusPrefix, rendered, StringComparison.Ordinal);

        _output.WriteLine("Webhook-callback proof: host listener captured the inbound POST → Pass.");
    }

    // ── Test 2: failing db-assert.postgres renders a relational diff → Fail + diff ─────

    /// <summary>
    /// Runs the relational-diff capstone scenario against a real seeded postgres: the
    /// <c>db-assert.postgres</c> step asserts <c>status == SHIPPED</c> against a seeded
    /// <c>PENDING</c> row, so the column mismatch Fails and the terminal renderer draws the
    /// provider's <see cref="IStepDiffRenderer"/> relational table under the failed step.
    /// Asserts <see cref="Verdict.Fail"/> and that the rendered output contains the diff
    /// table (the <c>column</c>/<c>expected</c>/<c>actual</c> headers and the SHIPPED vs
    /// PENDING values).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint07Capstone_FailingDbAssert_RendersRelationalDiff_Fail()
    {
        // ── Temp seed base directory with the orders SQL fixture ──────────────────
        var baseDir = Path.Combine(
            Path.GetTempPath(), "vouchfx-s7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var fixturesDir = Path.Combine(baseDir, "fixtures");
        Directory.CreateDirectory(fixturesDir);
        await File.WriteAllTextAsync(
            Path.Combine(baseDir, "fixtures", "orders.sql"), OrdersSql);

        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var verdict = await ScenarioRunner.RunAsync(
                yamlText: DiffYaml,
                scenarioName: "sprint-07-capstone-diff",
                providerAssemblies: s_providerAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                seedBaseDirectory: baseDir,
                cancellationToken: cts.Token);

            var rendered = sw.ToString();
            _output.WriteLine("=== Terminal output ===");
            _output.WriteLine(rendered);
            _output.WriteLine($"Verdict: {verdict}");

            // ── 1. The scenario fails (the asserted column value does not match) ──
            // status SHIPPED expected, PENDING observed → a column-mismatch Fail (NOT an
            // EnvironmentError: the DB connected and the query ran; the row simply differs).
            Assert.Equal(Verdict.Fail, verdict);

            // ── 2. The failed step is rendered ───────────────────────────────────
            Assert.Contains(AssertStepId, rendered, StringComparison.Ordinal);
            Assert.Contains("FAIL", rendered, StringComparison.OrdinalIgnoreCase);

            // ── 3. The relational diff table is rendered UNDER the failed step ────
            // The TerminalRenderer feeds the step's column-mismatch observation to the
            // db-assert.postgres IStepDiffRenderer, which draws a column | expected | actual
            // table.  Assert the headers AND both data values appear in the rendered output.
            Assert.Contains("column", rendered, StringComparison.Ordinal);
            Assert.Contains("expected", rendered, StringComparison.Ordinal);
            Assert.Contains("actual", rendered, StringComparison.Ordinal);
            Assert.Contains("status", rendered, StringComparison.Ordinal);   // the mismatched column name
            Assert.Contains("SHIPPED", rendered, StringComparison.Ordinal);  // expected value
            Assert.Contains("PENDING", rendered, StringComparison.Ordinal);  // observed value

            _output.WriteLine(
                "Relational-diff proof: failed db-assert.postgres rendered a column/expected/actual table.");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch (IOException) { }
        }
    }
}
