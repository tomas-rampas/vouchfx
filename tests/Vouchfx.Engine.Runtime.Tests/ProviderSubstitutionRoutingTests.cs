// S07-B-02b — per-provider substitution-routing EMIT assertions (non-docker).
//
// PURPOSE (Deliverable 1, second half — the cross-provider audit):
//   The SubstitutionUniformityTests prove the SHARED helper is the single source of truth.
//   This file proves every Core provider actually ROUTES its author-text fields through one
//   of those shared helpers, by reading each provider's Emit() output and asserting the
//   emitted CSX contains the resolve call for each substitutable field.  If a provider field
//   that SHOULD substitute does not route through a helper, the corresponding assertion here
//   fails — surfacing the gap rather than silently passing.
//
// ROUTING MAP (verified against the provider sources, S07 Sprint 5/6/7):
//   http.rest        path / each header value / body  → Secret_Helpers.ResolveTemplate
//                    (single pass: {placeholder} + ${secret:…}) inside HttpRest_Helpers.
//   mq-publish.kafka topic / key / payload / header values / avro record values
//                    → Secret_Helpers.ResolveTemplate inside MqPublishKafka_Helpers.
//   mq-expect.kafka  topic / key / payloadContains / header values / json expect values
//                    → Secret_Helpers.ResolveTemplate inside MqExpectKafka_Helpers.
//   db-assert.postgres param VALUES / expect-row VALUES → Substitute_Helpers.Resolve
//                    (call-site expressions in the StatementBlock); query TEXT →
//                    Substitute_Helpers.ResolveIdentifier inside DbAssertPostgres_Helpers
//                    (same {placeholder} semantics + a SQL-identifier charset guard, H1).
//                    db-assert intentionally separates substitution from secret resolution:
//                    values flow to AddWithValue (parameterised) / are compared in-memory, so
//                    Substitute_Helpers.Resolve (no ${secret} pass) is the correct helper.
//   script.csharp    NONE — author code is verbatim; covered by SubstitutionUniformityTests
//                    .ScriptCsharpBoundary_* (the deliberate escape hatch; not asserted here).
//
// All assertions read fragment.RequiredHelpers (the helper body, where ResolveTemplate /
// ResolveIdentifier live) and fragment.StatementBlock (where the db-assert call-site Resolve
// expressions live).  No Docker, no execution — pure Emit inspection.
using System;
using System.Collections.Generic;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// S07-B-02b: per-provider EMIT assertions proving each substitutable author-text field is
/// routed through the shared <c>Secret_Helpers.ResolveTemplate</c> or
/// <c>Substitute_Helpers.Resolve</c>/<c>ResolveIdentifier</c> helper.
/// </summary>
public sealed class ProviderSubstitutionRoutingTests
{
    // ── http.rest: path + each header value + body → Secret_Helpers.ResolveTemplate ──

    /// <summary>
    /// http.rest routes the path, every header value, and the body through
    /// <c>Secret_Helpers.ResolveTemplate(secrets, vars, …)</c> inside its helper — the single
    /// pass over the original template handling both <c>{placeholder}</c> and
    /// <c>${secret:…}</c>.  Header NAMES are intentionally NOT resolved (verbatim, §17).
    /// </summary>
    [Fact]
    public void HttpRest_RoutesPathHeadersBody_ThroughResolveTemplate()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "POST",
            Path: "/orders/{orderId}",
            Headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer ${secret:env/TOK}",
            },
            Body: "{\"ref\":\"{orderId}\"}",
            Expect: new HttpExpect(Status: 200));

        var fragment = provider.Emit(model, new StubCtx("http-step"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);

        // Path, header value, and body are each resolved via ResolveTemplate (the helper
        // names the template parameters pathTemplate / headerValueTemplates / bodyTemplate).
        // The header-value call is line-wrapped in the source, so the assertion is whitespace-
        // insensitive (the call-and-args substring is unambiguous regardless of wrapping).
        Assert.Contains("Secret_Helpers.ResolveTemplate(secrets, vars, pathTemplate)", helpers, StringComparison.Ordinal);
        Assert.Contains("secrets, vars, headerValueTemplates[hi])", helpers, StringComparison.Ordinal);
        Assert.Contains("Secret_Helpers.ResolveTemplate(secrets, vars, bodyTemplate)", helpers, StringComparison.Ordinal);

        // The shared helper sources are present (and dedupe across the suite).
        Assert.Contains(SecretHelper.Source, fragment.RequiredHelpers);
        Assert.Contains(SubstituteHelper.Source, fragment.RequiredHelpers);

        // The author-text tokens survive as LITERAL text in the emitted block (resolved at
        // runtime, never baked at emit time).
        Assert.Contains("{orderId}", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("${secret:env/TOK}", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── mq-publish.kafka: topic/key/payload/header values → ResolveTemplate ──

    /// <summary>
    /// mq-publish.kafka routes topic, key, payload, and each header value through
    /// <c>Secret_Helpers.ResolveTemplate</c> inside its helper (single pass).
    /// </summary>
    [Fact]
    public void MqPublishKafka_RoutesTopicKeyPayloadHeaders_ThroughResolveTemplate()
    {
        var provider = new MqPublishKafkaProvider();
        var model = new MqPublishKafkaModel(
            Target: "bus",
            Topic: "orders.{region}",
            Key: "{orderId}",
            Payload: "{\"token\":\"${secret:env/TOK}\"}",
            Headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["trace"] = "{traceId}",
            });

        var fragment = provider.Emit(model, new StubCtx("pub-step"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("var topic = Secret_Helpers.ResolveTemplate(secrets, vars, topicTemplate);", helpers, StringComparison.Ordinal);
        Assert.Contains("Secret_Helpers.ResolveTemplate(secrets, vars, keyTemplate)", helpers, StringComparison.Ordinal);
        Assert.Contains("var payload = Secret_Helpers.ResolveTemplate(secrets, vars, payloadTemplate);", helpers, StringComparison.Ordinal);
        // Header value call is line-wrapped in the source — match whitespace-insensitively.
        Assert.Contains("secrets, vars, headerValueTemplates[hi])", helpers, StringComparison.Ordinal);

        Assert.Contains(SecretHelper.Source, fragment.RequiredHelpers);
    }

    // ── mq-expect.kafka: topic/key/payloadContains/header+json values → ResolveTemplate ──

    /// <summary>
    /// mq-expect.kafka routes topic, key, payloadContains, and each header / JSON expect
    /// value through <c>Secret_Helpers.ResolveTemplate</c> inside its helper.
    /// </summary>
    [Fact]
    public void MqExpectKafka_RoutesKeyPayloadHeadersJson_ThroughResolveTemplate()
    {
        var provider = new MqExpectKafkaProvider();
        var model = new MqExpectKafkaModel(
            Target: "bus",
            Topic: "orders.{region}",
            Match: new KafkaMatch(
                Key: "{orderId}",
                Headers: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["trace"] = "{traceId}",
                },
                PayloadContains: "${secret:env/TOK}",
                Json: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["$.status"] = "{expectedStatus}",
                }));

        var fragment = provider.Emit(model, new StubCtx("exp-step"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("Secret_Helpers.ResolveTemplate(secrets, vars, expectKeyTemplate)", helpers, StringComparison.Ordinal);
        Assert.Contains("Secret_Helpers.ResolveTemplate(secrets, vars, payloadContainsTemplate)", helpers, StringComparison.Ordinal);
        Assert.Contains("headerValues[hi] = Secret_Helpers.ResolveTemplate(secrets, vars, headerValueTemplates[hi]);", helpers, StringComparison.Ordinal);
        Assert.Contains("jsonValues[ji] = Secret_Helpers.ResolveTemplate(secrets, vars, jsonValueTemplates[ji]);", helpers, StringComparison.Ordinal);

        Assert.Contains(SecretHelper.Source, fragment.RequiredHelpers);
    }

    // ── db-assert.postgres: param/expect VALUES → Substitute_Helpers.Resolve;
    //    query TEXT → Substitute_Helpers.ResolveIdentifier ──

    /// <summary>
    /// db-assert.postgres routes each parameter VALUE and each expect-row VALUE through
    /// <c>Substitute_Helpers.Resolve(Vars, …)</c> at the call site (the resolved value flows
    /// to <c>AddWithValue</c> / is compared in-memory), and the query TEXT through
    /// <c>Substitute_Helpers.ResolveIdentifier(vars, query)</c> inside its helper (the H1
    /// SQL-identifier-charset guard).  db-assert deliberately uses the placeholder-only
    /// helper — secret resolution is separated because values are parameterised.
    /// </summary>
    [Fact]
    public void DbAssertPostgres_RoutesParamAndExpectValues_ThroughSubstituteResolve()
    {
        var provider = new DbAssertPostgresProvider();
        var model = new DbAssertPostgresModel(
            Target: "db",
            Query: "SELECT status FROM {schema}.orders WHERE id = @id",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = "{orderId}",
            },
            Expect: new PostgresExpectation(
                RowCount: 1,
                Row: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = "{expectedStatus}",
                }));

        var fragment = provider.Emit(model, new StubCtx("db-step"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var block = fragment.StatementBlock;

        // Param VALUE and expect-row VALUE are wrapped at the call site (StatementBlock).
        Assert.Contains("Substitute_Helpers.Resolve(Vars, \"{orderId}\")", block, StringComparison.Ordinal);
        Assert.Contains("Substitute_Helpers.Resolve(Vars, \"{expectedStatus}\")", block, StringComparison.Ordinal);

        // The query TEXT is resolved via ResolveIdentifier inside the helper (H1 guard).
        Assert.Contains("Substitute_Helpers.ResolveIdentifier(vars, query)", helpers, StringComparison.Ordinal);

        Assert.Contains(SubstituteHelper.Source, fragment.RequiredHelpers);
    }

    // ── Cross-provider: the SHARED helper source is byte-identical wherever it appears ──

    /// <summary>
    /// The <c>Secret_Helpers</c> source emitted by http.rest, mq-publish.kafka, and
    /// mq-expect.kafka is byte-identical (the single source of truth, §13.3.1 dedup), and the
    /// <c>Substitute_Helpers</c> source emitted by http.rest and db-assert.postgres is
    /// byte-identical too.  This is why "which provider would call it" cannot change the
    /// resolved result — they all splice the SAME constant from <c>Vouchfx.Sdk</c>.
    /// </summary>
    [Fact]
    public void SharedHelperSources_AreByteIdentical_AcrossProviders()
    {
        var http = new HttpRestProvider().Emit(
            new HttpRestModel("svc", "GET", "/{p}", null, null, null), new StubCtx("h"));
        var pub = new MqPublishKafkaProvider().Emit(
            new MqPublishKafkaModel("bus", "{t}", null, "{p}", null), new StubCtx("p"));
        var exp = new MqExpectKafkaProvider().Emit(
            new MqExpectKafkaModel("bus", "{t}", new KafkaMatch("{k}", null, null, null)),
            new StubCtx("e"));
        var db = new DbAssertPostgresProvider().Emit(
            new DbAssertPostgresModel("db", "SELECT 1", null, new PostgresExpectation(1, null)),
            new StubCtx("d"));

        string Secret(CsxFragment f) => f.RequiredHelpers.Single(h => h.StartsWith("static class Secret_Helpers", StringComparison.Ordinal));
        string Subst(CsxFragment f) => f.RequiredHelpers.Single(h => h.StartsWith("static class Substitute_Helpers", StringComparison.Ordinal));

        // Secret_Helpers byte-identical across the three secret-aware providers + the SDK constant.
        Assert.Equal(SecretHelper.Source, Secret(http), StringComparer.Ordinal);
        Assert.Equal(SecretHelper.Source, Secret(pub), StringComparer.Ordinal);
        Assert.Equal(SecretHelper.Source, Secret(exp), StringComparer.Ordinal);

        // Substitute_Helpers byte-identical across http.rest + db-assert + the SDK constant.
        Assert.Equal(SubstituteHelper.Source, Subst(http), StringComparer.Ordinal);
        Assert.Equal(SubstituteHelper.Source, Subst(db), StringComparer.Ordinal);
    }

    // ── stub compile context ──────────────────────────────────────────────────────

    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(string stepId) => StepId = stepId;

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }
}
