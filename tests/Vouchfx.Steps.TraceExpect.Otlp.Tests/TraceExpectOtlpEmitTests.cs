// Tests for TraceExpectOtlpProvider — CSX emitter + compile-and-run round-trip.
//
// All tests are non-docker AND require NO real OTLP receiver: the compile round-trip runs the
// emitted CSX against a ScriptGlobalVariables built with a STUB ITraceCaptureAccessor that
// returns pre-seeded CapturedSpans. This proves the emitted CSX compiles, that the match logic
// (including traceparent extraction and the mini dotted-path capture evaluator) is correct, and
// — critically — that the redaction contract holds (no captured span data ever reaches a Fail
// observation, §17). Mirrors WebhookListenHttpEmitTests.cs's structure exactly (S07-F-01a
// precedent).
//
// Emit lint:
//   1. StatementBlock begins and ends with a brace.
//   2. No 'using var' anywhere.
//   3. Helper class is named 'TraceExpectOtlp_Helpers' (§13.3.1 prefix rule).
//   4. Secret_Helpers source is present in RequiredHelpers (Substitute_Helpers is NOT needed —
//      ResolveTemplate is self-contained).
//   5. A hyphenated step id is sanitised to the outcome key '__outcome::assert_trace'.
//   6. An absent method/service/spanName is emitted as a bare 'null' literal.
//
// Compile round-trip (no docker, no real receiver — stub accessor):
//   7. A captured span matching every criterion → Pass.
//   8. No captured span → Fail, matchedCriteria:0.
//   9. A partially-matching span → Fail with the best partial matchedCriteria count.
//  10. Full W3C traceparent criterion extracts the 32-hex trace id and matches a span whose
//      TraceId is that bare segment.
//  11. Attribute subset matching: declared attributes must ALL match; extra span attributes
//      are ignored.
//  12. capture: pulls the matched span's fields via the mini dotted-path evaluator → Pass +
//      captured Vars.
//  13. capture: an XPath-format entry against a trace-expect.otlp span is always unmet →
//      Inconclusive.
//  14. Secret path: an expected value with ${secret:env/MISSING} + a real env SecretAccessor →
//      EnvironmentError, reference-only.
//  15. No leak: a Fail observation never contains a span's trace id, name, service, or
//      attribute values — only counts.
//  16. Validate: a match with no traceId is REJECTED (traceId is required, not merely "at
//      least one of several criteria") — the enforcement half of the family's
//      no-forged-match security posture.
//  17. Eviction diagnostic: when the accessor reports TotalReceived > the retained/scanned
//      count, the Fail observation's "evicted" field surfaces the difference.
using System;
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.TraceExpect.Otlp;
using Xunit;

namespace Vouchfx.Steps.TraceExpect.Otlp.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="TraceExpectOtlpProvider"/> covering the
/// emitter (<see cref="IStepCompiler{TModel}"/>) and a full compile-and-run round-trip against a
/// stub <see cref="ITraceCaptureAccessor"/> (no real receiver).
/// </summary>
public sealed class TraceExpectOtlpEmitTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────────

    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId, IReadOnlyDictionary<string, CaptureExpr>? captures = null)
        {
            StepId = stepId;
            CaptureExprs = captures ?? new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    private sealed class StubTraceAccessor : ITraceCaptureAccessor
    {
        private static readonly IReadOnlyList<CapturedSpan> Empty = Array.Empty<CapturedSpan>();
        private readonly string _receiverName;
        private readonly IReadOnlyList<CapturedSpan> _captured;
        private readonly long _totalReceived;

        /// <param name="totalReceived">
        /// Overrides the TOTAL-received count reported by <see cref="GetTotalReceived"/>;
        /// defaults to <c>captured.Count</c> (no simulated eviction) when omitted. Pass a value
        /// greater than <c>captured.Count</c> to simulate a ring-buffer that has already
        /// evicted some spans.
        /// </param>
        public StubTraceAccessor(string receiverName, IReadOnlyList<CapturedSpan> captured, long? totalReceived = null)
        {
            _receiverName = receiverName;
            _captured = captured;
            _totalReceived = totalReceived ?? captured.Count;
        }

        public IReadOnlyList<CapturedSpan> GetCaptured(string receiverVarName) =>
            string.Equals(receiverVarName, _receiverName, StringComparison.Ordinal) ? _captured : Empty;

        public long GetTotalReceived(string receiverVarName) =>
            string.Equals(receiverVarName, _receiverName, StringComparison.Ordinal) ? _totalReceived : 0;
    }

    private readonly TraceExpectOtlpProvider _provider = new();

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(
            MakeModel("traces", new TraceMatch("abc", null, null, null)),
            new StubCompileContext("assert-trace"));

        var block = fragment.StatementBlock.Trim();
        Assert.StartsWith("{", block, StringComparison.Ordinal);
        Assert.EndsWith("}", block, StringComparison.Ordinal);
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" };
        var fragment = _provider.Emit(
            MakeModel("traces", new TraceMatch("abc", "svc", "op", attrs)),
            new StubCompileContext("my-step"));

        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);
        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsTraceExpectOtlpPrefixedClass()
    {
        var fragment = _provider.Emit(
            MakeModel("traces", new TraceMatch("abc", null, null, null)),
            new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("TraceExpectOtlp_Helpers", StringComparison.Ordinal));
    }

    // ── 4. RequiredHelpers includes Secret_Helpers ────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSecretHelperSource()
    {
        var fragment = _provider.Emit(
            MakeModel("traces", new TraceMatch("abc", null, null, null)),
            new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 5. Step-id sanitisation ──────────────────────────────────────────────────

    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "assert-trace";
        var fragment = _provider.Emit(
            MakeModel("traces", new TraceMatch("abc", null, null, null)),
            new StubCompileContext(rawId));

        Assert.Contains("__outcome::assert_trace", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. Absent criteria → bare 'null' literal ─────────────────────────────────

    [Fact]
    public void Emit_AbsentCriteria_EmitsNullLiterals()
    {
        // traceId is always present now (required, see the Validate tests below); this test
        // proves the OTHER, still-optional criteria (service is absent here, spanName is set)
        // emit a bare 'null' literal — not that Emit itself enforces the traceId requirement
        // (Validate does).
        var fragment = _provider.Emit(
            MakeModel("traces", new TraceMatch("abc", null, "op", null)),
            new StubCompileContext("k"));

        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 7. Compile round-trip: matching span → Pass ──────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_MatchingCapturedSpan_ReturnsPass()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch(
            TraceId: "4bf92f3577b34da6a3ce929d0e0e4736",
            Service: "orders-api",
            SpanName: "POST /orders",
            Attributes: null));

        var captured = new[]
        {
            MakeSpan("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1111111111111111", "unrelated", "other-svc"),
            MakeSpan("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7", "POST /orders", "orders-api"),
        };

        var outcome = await RunAsync(model, stepId, receiver, captured);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("\"matched\":true", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"scanned\":2", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 8. No captured span → Fail, matchedCriteria:0 ────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_NoCapturedSpan_ReturnsFail()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch("abc", null, null, null));

        var outcome = await RunAsync(model, stepId, receiver, Array.Empty<CapturedSpan>());

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"matched\":false", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"scanned\":0", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"matchedCriteria\":0", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 9. Partial match reports the BEST partial matchedCriteria count ──────────

    [Fact]
    public async Task Emit_CompileAndRun_PartiallyMatchingSpan_ReturnsFail_WithBestPartialCount()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch(
            TraceId: "4bf92f3577b34da6a3ce929d0e0e4736",
            Service: "orders-api",
            SpanName: "POST /orders",
            Attributes: null));

        // TraceId matches; Service and SpanName do not → 1 of 3 criteria satisfied.
        var captured = new[]
        {
            MakeSpan("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7", "GET /health", "other-svc"),
        };

        var outcome = await RunAsync(model, stepId, receiver, captured);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"matchedCriteria\":1", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"totalCriteria\":3", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 10. Full W3C traceparent extraction ──────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_FullTraceparentValue_ExtractsTraceIdAndMatches()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch(
            TraceId: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            Service: null,
            SpanName: null,
            Attributes: null));

        var captured = new[]
        {
            MakeSpan("4bf92f3577b34da6a3ce929d0e0e4736", "spanX", "op", "svc"),
        };

        var outcome = await RunAsync(model, stepId, receiver, captured);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    // ── 11. Attribute subset matching ────────────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_AttributeSubsetMatch_ReturnsPass()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch(
            TraceId: "t", Service: null, SpanName: null,
            Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["http.method"] = "POST" }));

        var attrs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["http.method"] = "POST",
            ["http.status_code"] = "201",  // extra attribute, not asserted — subset match
        };
        var captured = new[] { MakeSpan("t", "s", "op", "svc", attrs) };

        var outcome = await RunAsync(model, stepId, receiver, captured);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Fact]
    public async Task Emit_CompileAndRun_AttributeMismatch_ReturnsFail()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch(
            TraceId: "t", Service: null, SpanName: null,
            Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["http.method"] = "POST" }));

        var attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["http.method"] = "GET" };
        var captured = new[] { MakeSpan("t", "s", "op", "svc", attrs) };

        var outcome = await RunAsync(model, stepId, receiver, captured);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
    }

    // ── 12. capture: mini dotted-path evaluator over the synthesised observation ─

    [Fact]
    public async Task Emit_CompileAndRun_CaptureSpanId_ReturnsPass_AndCapturesValue()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch("t", null, null, null));
        var captured = new[] { MakeSpan("t", "span-42", "op", "svc") };

        var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["capturedSpanId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.spanId"),
        };

        var (outcome, vars) = await RunWithVarsAsync(model, stepId, receiver, captured, captures);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal("span-42", vars["capturedSpanId"]);
    }

    [Fact]
    public async Task Emit_CompileAndRun_CaptureNestedAttribute_ReturnsPass_AndCapturesValue()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch("t", null, null, null));
        // Note: TryEvaluateDottedPath splits the capture expression on '.', so an attribute
        // KEY containing a literal dot (e.g. "order.id") is NOT reachable this way — exactly
        // the same limitation plain dot-notation JSONPath has for such keys (bracket notation
        // would be required, which this minimal evaluator does not support). Use a dot-free
        // attribute key here.
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["orderId"] = "ord-7" };
        var captured = new[] { MakeSpan("t", "s", "op", "svc", attrs) };

        var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["orderId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.attributes.orderId"),
        };

        var (outcome, vars) = await RunWithVarsAsync(model, stepId, receiver, captured, captures);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal("ord-7", vars["orderId"]);
    }

    // ── 13. An XPath-format capture is always unmet → Inconclusive ──────────────

    [Fact]
    public async Task Emit_CompileAndRun_XPathFormatCapture_IsAlwaysUnmet_ReturnsInconclusive()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch("t", null, null, null));
        var captured = new[] { MakeSpan("t", "s", "op", "svc") };

        var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["x"] = new CaptureExpr(CaptureFormat.XPath, "//span/id"),
        };

        var outcome = await RunAsync(model, stepId, receiver, captured, captures);

        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.Contains("captureUnmet", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 14. Secret path: missing ${secret:env/…} → EnvironmentError reference-only ─

    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInService_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_TRACE_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "assert-secret";
            const string receiver = "traces";

            // traceId is a literal (required, non-secret) value here — this test isolates the
            // ONE failure mode under test: a missing secret in the Service field.
            var model = MakeModel(receiver, new TraceMatch(
                TraceId: "t", Service: $"${{secret:env/{envName}}}", SpanName: null, Attributes: null));

            var csx = Assemble(model, stepId);
            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_additionalRefs);

            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor,
                Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
                new StubTraceAccessor(receiver, Array.Empty<CapturedSpan>()));

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcome = ReadOutcome(vars, stepId);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 15. No leak: Fail observation never carries span data — only counts ─────

    [Fact]
    public async Task Emit_CompileAndRun_FailObservation_NeverLeaksSpanData()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        const string secretSpanName = "SENSITIVE-OPERATION-NAME";
        var model = MakeModel(receiver, new TraceMatch(
            TraceId: "does-not-exist", Service: null, SpanName: null, Attributes: null));

        var captured = new[] { MakeSpan("real-trace-id-value", "real-span-id", secretSpanName, "real-service-name") };

        var outcome = await RunAsync(model, stepId, receiver, captured);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.DoesNotContain("real-trace-id-value", outcome.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain("real-span-id", outcome.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain(secretSpanName, outcome.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain("real-service-name", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 16. Validate: traceId is REQUIRED, not merely "at least one of several" ─────

    private sealed class StubProjectContext : IProjectContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
            new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
    }

    [Fact]
    public void Validate_MissingTraceId_ReturnsValidationFailure()
    {
        // Even with service/spanName/attributes ALL declared, a match with no traceId must be
        // REJECTED — this is the enforcement half of the no-forged-match security posture: a
        // match with no trace id would accept ANY span the SUT ever exports that happens to
        // match those refinements, from ANY trace, not the specific transaction under test.
        var model = MakeModel("traces", new TraceMatch(
            TraceId: null,
            Service: "orders-api",
            SpanName: "POST /orders",
            Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" }));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("traceId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EmptyTraceId_ReturnsValidationFailure()
    {
        // An empty (whitespace) traceId is equally rejected — not just a null one.
        var model = MakeModel("traces", new TraceMatch(
            TraceId: "   ", Service: null, SpanName: null, Attributes: null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("traceId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TraceIdPresent_NoOtherCriteria_Succeeds()
    {
        // traceId ALONE is a fully valid match — service/spanName/attributes are optional
        // refinements, never required alongside it.
        var model = MakeModel("traces", new TraceMatch(
            TraceId: "abc", Service: null, SpanName: null, Attributes: null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_EmptyReceiver_ReturnsValidationFailure()
    {
        var model = new TraceExpectOtlpModel(
            Receiver: string.Empty,
            Match: new TraceMatch(TraceId: "abc", Service: null, SpanName: null, Attributes: null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("receiver", StringComparison.Ordinal));
    }

    // ── 17. Eviction diagnostic: "evicted" surfaces on a Fail observation ───────────

    [Fact]
    public async Task Emit_CompileAndRun_TotalReceivedExceedsScanned_FailObservation_IncludesEvictedCount()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch("does-not-exist", null, null, null));

        // The buffer currently retains 3 spans (none matching), but the receiver has received
        // 12 in total — 9 were evicted by the ring buffer before this scan.
        var captured = new[]
        {
            MakeSpan("aaaa", "s1", "op1", "svc"),
            MakeSpan("bbbb", "s2", "op2", "svc"),
            MakeSpan("cccc", "s3", "op3", "svc"),
        };

        var fragment = _provider.Emit(model, new StubCompileContext(stepId));
        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            new StubTraceAccessor(receiver, captured, totalReceived: 12));

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcome = ReadOutcome(vars, stepId);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"scanned\":3", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"evicted\":9", outcome.Observation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_CompileAndRun_NoEviction_FailObservation_ReportsEvictedZero()
    {
        const string stepId = "assert-trace";
        const string receiver = "traces";
        var model = MakeModel(receiver, new TraceMatch("abc", null, null, null));

        // Default StubTraceAccessor: TotalReceived == captured.Count (no simulated eviction).
        var outcome = await RunAsync(model, stepId, receiver, Array.Empty<CapturedSpan>());

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"evicted\":0", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── Private harness ───────────────────────────────────────────────────────────

    private async Task<StepOutcome> RunAsync(
        TraceExpectOtlpModel model,
        string stepId,
        string receiver,
        IReadOnlyList<CapturedSpan> captured,
        IReadOnlyDictionary<string, CaptureExpr>? captures = null)
    {
        var (outcome, _) = await RunWithVarsAsync(model, stepId, receiver, captured, captures);
        return outcome;
    }

    private async Task<(StepOutcome Outcome, Dictionary<string, object?> Vars)> RunWithVarsAsync(
        TraceExpectOtlpModel model,
        string stepId,
        string receiver,
        IReadOnlyList<CapturedSpan> captured,
        IReadOnlyDictionary<string, CaptureExpr>? captures = null)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId, captures));
        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            new StubTraceAccessor(receiver, captured));

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        return (ReadOutcome(vars, stepId), vars);
    }

    private string Assemble(TraceExpectOtlpModel model, string stepId)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId));

        // Splice via CsxAssembler (not a manual join) — it declares the per-step
        // __stepCt_<safeId> local the emitted call site now references (§4 common
        // step fields, issue #232).
        return CsxAssembler.Assemble(new[] { (stepId, fragment) }).CsxSource;
    }

    private static StepOutcome ReadOutcome(Dictionary<string, object?> vars, string stepId)
    {
        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. Actual keys: [{string.Join(", ", vars.Keys)}]");
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    private static TraceExpectOtlpModel MakeModel(string receiver, TraceMatch match) =>
        new(Receiver: receiver, Match: match);

    private static CapturedSpan MakeSpan(
        string traceId, string spanId, string name, string serviceName,
        IReadOnlyDictionary<string, string>? attributes = null) =>
        new(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: string.Empty,
            Name: name,
            ServiceName: serviceName,
            Attributes: attributes ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StatusCode: "STATUS_CODE_OK",
            At: DateTimeOffset.UtcNow);
}
