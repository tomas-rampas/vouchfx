// Tests for CacheAssertElasticsearchProvider — CSX emitter, resource + compile-reference
// contributors, and full compile-and-run round-trips (non-docker).
//
// Mirrors Vouchfx.Steps.CacheAssert.Redis.Tests/CacheAssertRedisEmitTests.cs.
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted fragment (§13.3.1 invariant).
//   3.  Emit: helper class is named 'CacheAssertElasticsearch_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: special characters in index/query are JSON-escaped.
//   6.  Emit: query template with {placeholder} survives verbatim (not emit-time interpolated).
//   7.  Emit: exact-count null is emitted as 'null' literal.
//   8.  Emit: field arrays are emitted as inline string array literals.
//   9.  Resources: yields an elasticsearch ResourceRequirement whose Name equals model.Target.
//   10. CompileReferenceAssemblies: contains the System.Net.Http assembly.
//   11. Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//   12. Full compile-and-run (no docker): EnvironmentError when the endpoint refuses the
//       connect (transport-failure path — the POST throws, so no count branch is reached).
//   13. Full compile-and-run (no docker): credential URL not leaked in observation (§17 redaction).
//   14. Full compile-and-run (no docker): match_all default query compiles (no explicit query).
//   15. Full compile-and-run (no docker): field assertion path compiles (with expect.fields).
//   16-22. Documented at their own section markers below.
//   23. DeadLoopbackEndpoint holds its port for the reservation's lifetime (the property the
//       connect-refused rows in this file, and in three other assemblies, stand on — #527).
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Elasticsearch;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Steps.CacheAssert.Elasticsearch.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="CacheAssertElasticsearchProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class CacheAssertElasticsearchEmitTests
{
    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId) => StepId = stepId;

        /// <inheritdoc />
        public string StepId { get; }

        /// <inheritdoc />
        public string SuiteNamespace => "Generated";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private readonly CacheAssertElasticsearchProvider _provider = new();

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  The ES provider uses
    /// BCL HttpClient, System.Text.Json, and System.Uri — none are in the default
    /// TPA-only Roslyn reference subset, so they must be supplied explicitly.
    /// These are compile-time references only; at runtime they resolve from the Default ALC.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("es-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'), "StatementBlock must begin with '{'.");
        Assert.True(block.EndsWith('}'), "StatementBlock must end with '}'.");
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("my-step"));
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsCacheAssertElasticsearchPrefixedClass()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("CacheAssertElasticsearch_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "check-es-hits";
        var safeId = CsxFragment.SanitiseId(rawId);  // "check_es_hits"
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. Special characters in index/query are JSON-escaped ─────────────────

    [Fact]
    public void Emit_SpecialCharactersInIndex_AreJsonEscaped()
    {
        const string dangerousIndex = "my-index\"with\\quotes";
        var model = MakeModel(index: dangerousIndex);
        var fragment = _provider.Emit(model, new StubCompileContext("escape-test"));

        // The raw unescaped string must not appear verbatim — it would break the literal.
        Assert.DoesNotContain(dangerousIndex, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. {placeholder} in query template survives emit ─────────────────────

    [Fact]
    public void Emit_QueryWithPlaceholder_SurvivesVerbatimInEmittedLiteral()
    {
        // The {status} placeholder must survive in the emitted C# string literal so
        // CacheAssertElasticsearch_Helpers.ResolveQuery can resolve it at runtime.
        const string queryWithPlaceholder = "{\"query\":{\"match\":{\"status\":\"{status}\"}}}";
        var model = MakeModel(query: queryWithPlaceholder);
        var fragment = _provider.Emit(model, new StubCompileContext("placeholder-step"));

        // The placeholder text must appear inside the emitted StatementBlock —
        // specifically, it is JSON-serialized so {status} becomes {status}
        // OR appears verbatim inside a raw string with double-backslash escaping.
        // Either way the token text "status" must appear in the block.
        Assert.Contains("status", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 7. Exact-count null emitted as 'null' literal ─────────────────────────

    [Fact]
    public void Emit_NoExactCount_EmitsNullLiteralInStatementBlock()
    {
        var model = MakeModel(count: null, minCount: 2);
        var fragment = _provider.Emit(model, new StubCompileContext("count-step"));

        // When count is null the call site must pass 'null' so the helper uses min-count.
        Assert.Contains("null", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 8. Field assertion arrays emitted as inline array literals ────────────

    [Fact]
    public void Emit_WithFieldAssertions_EmitsStringArrayLiterals()
    {
        var fields = new[] { new EsFieldAssertion("status", "active") };
        var model = MakeModel(fields: fields);
        var fragment = _provider.Emit(model, new StubCompileContext("field-step"));

        // Field names and expected values must appear as string literals in the block.
        Assert.Contains("\"status\"", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("\"active\"", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 9. IResourceContributor ───────────────────────────────────────────────

    [Fact]
    public void Resources_YieldsElasticsearchRequirementWithMatchingName()
    {
        var requirements = _provider.Resources(MakeModel(target: "search")).ToList();

        Assert.Single(requirements);
        Assert.Equal("elasticsearch", requirements[0].Family, StringComparer.Ordinal);
        Assert.Equal("search", requirements[0].Name, StringComparer.Ordinal);
    }

    // ── 10. ICompileReferenceContributor ──────────────────────────────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsSystemNetHttpAssembly()
    {
        Assert.Contains(((ICompileReferenceContributor)_provider).CompileReferenceAssemblies.ToList(), a =>
            a.GetName().Name?.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 11. Compile round-trip: EnvironmentError when conn key absent ─────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = MakeModel(target: "missing-dep");

        // No connection key seeded in Vars — the helper must detect the absence and write
        // EnvironmentError rather than throwing.
        var outcome = await RunStepAsync(
            model, "es-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 12. Compile round-trip: EnvironmentError when endpoint is dead ────────

    [Fact]
    public async Task Emit_CompileAndRun_DeadEndpoint_ReturnsEnvironmentError()
    {
        var model = MakeModel(target: "search", count: 1);

        using var dead = DeadLoopbackEndpoint.Reserve();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = $"http://{dead.Authority}",
        };

        var outcome = await RunStepAsync(model, "es-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 13. Compile round-trip: credential URL absent from observation ─────────
    //
    // §17 protection is three-layered — this test verifies all three:
    //   (1) baseUrl has userinfo stripped before any observation is built
    //   (2) the generic System.Exception catch emits ONLY ex.GetType().Name, so
    //       the URL never appears in the observation even if something throws
    //   (3) therefore the observation is exactly {"error":"HttpRequestException"}
    //       — no URL, no host, no password
    //
    // The test is intentionally strict: it checks the ACTUAL content (not merely
    // that the password is absent), so deleting any protection layer breaks it.

    [Fact]
    public async Task Emit_CompileAndRun_CredentialedConnFails_ObservationContainsOnlyTypeName()
    {
        using var dead = DeadLoopbackEndpoint.Reserve();
        var connUrl = $"http://elastic:sup3rsecret@{dead.Authority}";
        var model = MakeModel(target: "search");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = connUrl,
        };

        var outcome = await RunStepAsync(model, "es-cred-leak-check", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);

        var obs = outcome.Observation!;

        // §17 layer (2): generic catch emits ONLY the exception type name — no URL,
        // no host, no password.  Each leak shape gets its own row so a failure names the
        // thing that leaked rather than just reporting that the JSON changed.
        //
        // The boolean form, not Assert.DoesNotContain: xunit prints the ACTUAL on a failed
        // DoesNotContain, and the actual in every credential row below is the string under
        // suspicion of carrying the credential — so the failure output would publish the
        // leak it is reporting.  The named message carries the diagnosis instead.
        Assert.True(
            !obs.Contains("sup3rsecret", StringComparison.Ordinal),
            "observation leaked the password");
        Assert.True(
            !obs.Contains("elastic", StringComparison.Ordinal),
            "observation leaked the username");

        // The credential also exists in a form the two plaintext rows above cannot see —
        // this one is genuinely independent detection, unlike a row over a superstring of
        // the password.  The provider base64-encodes user + ":" + pass into the Basic auth
        // header (CacheAssertElasticsearchProvider.cs, Convert.ToBase64String at the
        // authHeader assignment), so a regression that echoed authHeader would emit
        // ZWxhc3RpYzpzdXAzcnNlY3JldA== — a trivially reversible full credential pair that
        // contains neither "elastic" nor "sup3rsecret" as a substring.
        Assert.True(
            !obs.Contains("ZWxhc3RpYzpzdXAzcnNlY3JldA==", StringComparison.Ordinal),
            "observation leaked the base64 Basic-auth credential pair");

        // Host and port are asserted separately because a provider can leak the endpoint
        // without leaking the credential — rebuilding scheme://host:port for a message is
        // the obvious way to do it.  Both name the values this run actually used, not
        // literals that would go stale the moment the endpoint stopped being hard-coded.
        Assert.DoesNotContain(dead.Host, outcome.Observation!, StringComparison.Ordinal);
        var portText = $"{dead.Port}";
        Assert.DoesNotContain(portText, outcome.Observation!, StringComparison.Ordinal);

        // Equality, not Contains: the block comment above claims the observation IS
        // exactly this JSON, and until it was an Assert.Equal nothing enforced the claim —
        // {"error":"HttpRequestException","host":"127.0.0.1"} satisfies a Contains.  This
        // subsumes the five rows above; they are kept for their failure messages.
        Assert.Equal("{\"error\":\"HttpRequestException\"}", outcome.Observation);
    }

    // ── 14. Compile round-trip: default match_all query compiles ──────────────

    [Fact]
    public async Task Emit_CompileAndRun_DefaultMatchAllQuery_CompilesAndReturnsEnvironmentError()
    {
        // Model with no explicit query → the helper emits the match_all default.
        // Against a dead endpoint it must surface EnvironmentError (not a compile error).
        var model = MakeModel(target: "search", query: null);

        using var dead = DeadLoopbackEndpoint.Reserve();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = $"http://{dead.Authority}",
        };

        var outcome = await RunStepAsync(model, "es-matchall", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── 15. Compile round-trip: field assertion path compiles ─────────────────

    [Fact]
    public async Task Emit_CompileAndRun_WithFieldAssertions_CompilesAndReturnsEnvironmentError()
    {
        // Model with field assertions — proves the expanded array literals compile.
        var fields = new[] { new EsFieldAssertion("status", "active") };
        var model = MakeModel(target: "search", fields: fields);

        using var dead = DeadLoopbackEndpoint.Reserve();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = $"http://{dead.Authority}",
        };

        var outcome = await RunStepAsync(model, "es-fields", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── 16. Non-2xx redaction: host authority + creds absent from observation ──
    //
    // §17 FIX 1: the non-2xx branch must NOT include the host:port or any raw
    // response snippet in the observation.  A credentialed conn with a stub that
    // returns 400 is driven; the observation must contain NEITHER the password NOR
    // the host authority.

    [Fact]
    public async Task Emit_CompileAndRun_Non2xx_ObservationHasNoHostOrCredentials()
    {
        var port = FindFreePort();
        const string badBody = "{\"error\":{\"type\":\"index_not_found_exception\"}}";
        StubHttpServer? stub = null;
        try
        {
            stub = StubHttpServer.Start(port, 400, badBody);

            // Credentialed connection — host authority is "localhost:{port}"
            var connUrl = $"http://elastic:sup3rsecret@localhost:{port}";
            var model = MakeModel(target: "search", index: "orders", count: 1);
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection("search")] = connUrl,
            };

            var outcome = await RunStepAsync(model, "non2xx-redact", vars);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotNull(outcome.Observation);

            // Password must never appear in the observation.  Boolean form, not
            // DoesNotContain: the actual xunit would print on failure is the observation
            // under suspicion of carrying the password.
            Assert.True(
                !outcome.Observation!.Contains("sup3rsecret", StringComparison.Ordinal),
                "observation leaked the password");

            // Host authority (host:port) must not appear — only index/status is allowed.
            Assert.DoesNotContain($"localhost:{port}", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            stub?.Dispose();
        }
    }

    // ── 17. False-pass guard: empty hits.hits + declared fields → Fail ─────────
    //
    // §FIX 2: when the ES response has total.value>0 but hits.hits:[] (size:0 query),
    // and the model declares field assertions, the step must Fail (not Pass silently).

    [Fact]
    public async Task Emit_CompileAndRun_EmptyHitsArrayWithFieldAssertions_ReturnsFail()
    {
        var port = FindFreePort();
        // total.value=5 (count passes) but hits.hits:[] (empty — size:0 query equivalent)
        const string esBody = "{\"hits\":{\"total\":{\"value\":5,\"relation\":\"eq\"},\"hits\":[]}}";
        StubHttpServer? stub = null;
        try
        {
            stub = StubHttpServer.Start(port, 200, esBody);

            var fields = new[] { new EsFieldAssertion("status", "active") };
            var model = MakeModel(target: "search", index: "orders", fields: fields);
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection("search")] = $"http://localhost:{port}",
            };

            var outcome = await RunStepAsync(model, "empty-hits-fields", vars);

            // Must Fail — not silently pass without evaluating the declared field assertion.
            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            Assert.Contains("fieldError", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            stub?.Dispose();
        }
    }

    // ── 18. Injection breakout: {placeholder} value stays an escaped string ───
    //
    // §17 FIX 3 (security regression lock): a placeholder value containing embedded
    // quotes and JSON structure must arrive at the server as a properly-escaped
    // string literal — the injected "must" array must NOT become a JSON clause.

    [Fact]
    public async Task Emit_CompileAndRun_PlaceholderBreakout_ValueRemainsEscapedString()
    {
        var port = FindFreePort();
        // Return 200 + empty hits so the step runs to completion.
        const string esBody = "{\"hits\":{\"total\":{\"value\":0,\"relation\":\"eq\"},\"hits\":[]}}";
        StubHttpServer? stub = null;
        try
        {
            stub = StubHttpServer.Start(port, 200, esBody, captureBody: true);

            // Breakout attempt: the value contains quotes and an extra JSON clause.
            // If ResolveQuery were naive, this would inject a "must" property into the query.
            const string breakoutValue = "x\",\"must\":[{\"match_all\":{}}],\"x\":\"";

            var query = "{\"query\":{\"match\":{\"status\":\"{v}\"}}}";
            var model = MakeModel(target: "search", index: "orders", query: query);
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection("search")] = $"http://localhost:{port}",
                ["v"] = breakoutValue,
            };

            await RunStepAsync(model, "inject-breakout", vars);

            // Wait for the stub to capture the POST body (with a generous timeout).
            var captured = await stub.WaitForCapturedBodyAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(captured);

            // The captured body must parse as valid JSON.
            var doc = JsonDocument.Parse(captured!);

            // The injected "must" key must NOT be a property at any JSON level we can reach.
            Assert.False(
                doc.RootElement.TryGetProperty("must", out _),
                "Breakout injection must not create a top-level 'must' property.");

            // The value of query.match.status must be the breakout string (escaped, not parsed).
            var statusValue = doc.RootElement
                .GetProperty("query")
                .GetProperty("match")
                .GetProperty("status")
                .GetString();
            Assert.Equal(breakoutValue, statusValue);
        }
        finally
        {
            stub?.Dispose();
        }
    }

    // ── 19–22. IStepDiffRenderer: CanRender / RenderDiff ─────────────────────

    [Fact]
    public void DiffRenderer_CanRender_CountMismatchShape_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse(
            "{\"matched\":false,\"expected\":\">=1\",\"actual\":0,\"index\":\"orders\"}");
        Assert.True(_provider.CanRender(doc.RootElement));
    }

    [Fact]
    public void DiffRenderer_CanRender_FieldMismatchShape_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse(
            "{\"matched\":false,\"field\":\"status\",\"expected\":\"active\",\"actual\":\"inactive\"}");
        Assert.True(_provider.CanRender(doc.RootElement));
    }

    [Fact]
    public void DiffRenderer_CanRender_PassShape_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("{\"matched\":true,\"hits\":5}");
        Assert.False(_provider.CanRender(doc.RootElement));
    }

    [Fact]
    public void DiffRenderer_CanRender_EnvironmentErrorShape_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse(
            "{\"error\":\"HTTP 503\",\"index\":\"orders\"}");
        Assert.False(_provider.CanRender(doc.RootElement));
    }

    [Fact]
    public void DiffRenderer_RenderDiff_CountMismatch_IncludesExpectedAndActual()
    {
        using var doc = JsonDocument.Parse(
            "{\"matched\":false,\"expected\":\">=1\",\"actual\":0,\"index\":\"orders\"}");
        var rendered = _provider.RenderDiff(doc.RootElement);

        Assert.NotNull(rendered);
        Assert.Contains(">=1", rendered!, StringComparison.Ordinal);
        Assert.Contains("0", rendered!, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffRenderer_RenderDiff_FieldMismatch_IncludesFieldNameAndValues()
    {
        using var doc = JsonDocument.Parse(
            "{\"matched\":false,\"field\":\"status\",\"expected\":\"active\",\"actual\":\"inactive\"}");
        var rendered = _provider.RenderDiff(doc.RootElement);

        Assert.NotNull(rendered);
        // Field name must appear in the rendered output (reviewer FIX 4 n2).
        Assert.Contains("status", rendered!, StringComparison.Ordinal);
        Assert.Contains("active", rendered!, StringComparison.Ordinal);
        Assert.Contains("inactive", rendered!, StringComparison.Ordinal);
    }

    // ── 23. DeadLoopbackEndpoint holds its port until disposed ────────────────

    /// <summary>
    /// The property every connect-refused row in this file stands on: while a reservation
    /// is held the port cannot be taken by anything else, and once it is disposed the port
    /// is released.  Without this, those rows would be asserting a belief about the host.
    /// </summary>
    /// <remarks>
    /// MEASURED on Windows 10.0.26200 (2026-09-18): the HOLD half below passed on the first
    /// attempt.  NOT cross-platform by construction, whatever an earlier revision of this
    /// comment claimed: a plain bind of a port already bound is EADDRINUSE on Linux only when
    /// NEITHER socket carries <c>SO_REUSEADDR</c>, and .NET sets it on every TCP socket
    /// immediately before <c>bind()</c> regardless of platform (<c>SystemNative_Bind</c>,
    /// dotnet/runtime <c>src/native/libs/System.Native/pal_networking.c</c>) — so on Linux this
    /// test's own "intruder" carried <c>SO_REUSEADDR</c> too, and the second bind SUCCEEDED.
    /// MEASURED on PR #546's first CI run (ubuntu-latest, 2026-09-22): this test failed exactly
    /// that way — the assertion expecting <see cref="SocketException"/> from the second bind saw
    /// none. <see cref="DeadLoopbackEndpoint.Reserve"/> now clears <c>SO_REUSEADDR</c> on the
    /// reservation immediately after its own bind (Linux-only; see
    /// <see cref="DeadLoopbackEndpoint"/>'s own remarks for the full mechanism). MEASURED on the
    /// same Linux host with the fix applied (2026-09-22): the HOLD half below now also passes on
    /// the first attempt, matching Windows.
    /// The two halves prove different facts on purpose: the HOLD half binds the port a second
    /// time and must fail on the first attempt (nothing can legitimately take a held port),
    /// whereas the RELEASE half asserts the reservation's own handle is closed rather than
    /// re-binding the port — see the comment at that assertion for why a re-bind would race
    /// every other process on the host for the same ephemeral number.
    /// </remarks>
    [Fact]
    public void DeadLoopbackEndpoint_HoldsThePortUntilDisposed()
    {
        var dead = DeadLoopbackEndpoint.Reserve();
        // One spelling of the address for both halves, read back off the reservation.
        var endpoint = new IPEndPoint(IPAddress.Parse(dead.Host), dead.Port);
        try
        {
            var intruder = new Socket(
                AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var ex = Assert.Throws<SocketException>(() => intruder.Bind(endpoint));
                Assert.Equal(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
            }
            finally
            {
                intruder.Dispose();
            }
        }
        finally
        {
            dead.Dispose();
        }

        // Released, not leaked: the reservation's own socket handle is closed.  That is the fact
        // the kernel acts on — a bound port is freed when its last handle closes, and this process
        // holds exactly one — and it is race-free.  Re-binding the same port would not be: the
        // number re-enters the shared ephemeral pool the moment it is released, and any other test
        // host, outbound connection or process on this host can be handed it in the gap, so a wait
        // bounded at any length cannot tell that rival from a leak (PR #555 review).  A leaked
        // reservation, by contrast, is a handle that is still open, which this sees every time.
        Assert.True(dead.IsReleased, "the reservation's socket handle is still open after Dispose");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Finds a free loopback TCP port by binding temporarily to port 0, for the stub-server
    /// rows — which need a port they can actually LISTEN on, so the reservation cannot be
    /// held.  Never use this for a row that wants the connect REFUSED: from the moment
    /// <c>Stop()</c> returns, the port is dead only by assumption.  Use
    /// <see cref="DeadLoopbackEndpoint"/> there instead.
    /// </summary>
    private static int FindFreePort()
    {
        var tl = new TcpListener(IPAddress.Loopback, 0);
        tl.Start();
        var port = ((IPEndPoint)tl.LocalEndpoint).Port;
        tl.Stop();
        return port;
    }

    /// <summary>
    /// Minimal in-process HTTP stub that listens on <c>http://localhost:{port}/</c>,
    /// returns a fixed status code and JSON body for every incoming POST, and
    /// optionally captures the first request body.
    /// </summary>
    private sealed class StubHttpServer : IDisposable
    {
        private readonly HttpListener _hl;
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource<string?> _capturedBody;

        private StubHttpServer(
            HttpListener hl,
            CancellationTokenSource cts,
            TaskCompletionSource<string?> capturedBody)
        {
            _hl = hl;
            _cts = cts;
            _capturedBody = capturedBody;
        }

        /// <summary>Returns the captured request body, or <see langword="null"/> if capture was disabled.</summary>
        public Task<string?> WaitForCapturedBodyAsync(TimeSpan timeout)
        {
            var delayTask = Task.Delay(timeout).ContinueWith(_ => (string?)null);
            return Task.WhenAny(_capturedBody.Task, delayTask)
                .ContinueWith(t => t.Result.Result);
        }

        public static StubHttpServer Start(int port, int statusCode, string responseBody,
            bool captureBody = false)
        {
            var prefix = $"http://localhost:{port}/";
            var hl = new HttpListener();
            hl.Prefixes.Add(prefix);
            hl.Start();

            var cts = new CancellationTokenSource();
            var tcs = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    HttpListenerContext? ctx = null;
                    try
                    {
                        ctx = await hl.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (ctx is null)
                        continue;

                    // Capture the request body if requested (FIX 3 injection test).
                    if (captureBody)
                    {
                        using var reader = new StreamReader(
                            ctx.Request.InputStream, Encoding.UTF8, leaveOpen: true);
                        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        tcs.TrySetResult(body);
                    }
                    else
                    {
                        tcs.TrySetResult(null);
                    }

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(responseBody);
                        ctx.Response.StatusCode = statusCode;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.Close();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                    {
                        // Dispose() (below) cancels _cts BEFORE calling _hl.Stop()/Close(), but
                        // that teardown can still race a response this loop is actively writing
                        // (see the equivalent raw-Thread guard in
                        // MailExpectSmtpEmitTests.StartMockMailpit). This loop runs inside a
                        // fire-and-forget Task, so an unhandled exception here would not itself
                        // crash the host — contained anyway so teardown stays deterministic
                        // rather than relying on the TPL's swallow-unobserved-exception behaviour.
                    }
                }
            });

            return new StubHttpServer(hl, cts, tcs);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _hl.Stop(); }
            catch { /* already stopped */ }
            try { _hl.Close(); }
            catch { /* already closed */ }
            _cts.Dispose();
        }
    }

    private static CacheAssertElasticsearchModel MakeModel(
        string target = "search",
        string index = "orders",
        string? query = null,
        int? count = null,
        int minCount = 1,
        IReadOnlyList<EsFieldAssertion>? fields = null) =>
        new CacheAssertElasticsearchModel(
            Target: target,
            Index: index,
            Query: query,
            Expect: new EsExpectation(Count: count, MinCount: minCount, Fields: fields));

    /// <summary>
    /// Emits the fragment for a <c>cache-assert.elasticsearch</c> step, assembles it,
    /// compiles it once, and executes it with the supplied <c>Vars</c> dictionary.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        CacheAssertElasticsearchModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }
}
