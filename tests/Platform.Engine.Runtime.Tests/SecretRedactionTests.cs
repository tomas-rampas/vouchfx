// S05-G-01 — report-layer redaction hook (non-docker).
//
// Guarantees that a resolved secret VALUE never reaches the event stream, the
// terminal renderer, or the captured-var trace, AND that the already-reserved
// SubstitutionRef.SecretDerived wire field is lit up for a step that references a
// secret — using only the reference (source/path), never the value (§17, docs/02 §14.5).
//
// Why these tests do NOT stand up a topology:
//   The runner derives substitution/capture provenance and emits StepCompletedEvent
//   only AFTER the isolated Roslyn delegate has run against a live topology
//   (RunScenarioAgainstTopologyAsync), which requires Docker. Per the S05-G-01 brief,
//   we therefore assert the redaction + SecretDerived wiring at the event-derivation
//   layer — the same code path the runner uses — WITHOUT containers, mirroring how the
//   existing non-docker ScenarioRunner tests (RunAsync_*_NoTopology) avoid Docker:
//     • ScenarioRunner.DeriveSubstitutionProvenance is the exact internal method the
//       per-step loop calls; we feed it real StepNodes built from YAML via the live
//       parser + AstBuilder, then build the real StepCompletedEvent JSON line via
//       EventStreamJson.ToLine (the same emission the runner performs) and parse it back.
//   The full live-HTTP end-to-end no-leak proof (a step whose request actually fires
//   with a revealed Authorization header) is the Docker M2 scenario S05-D-01.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Compilation;
using Platform.Engine.Reporting;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker tests for the S05-G-01 report-layer redaction hook: secret values
/// never reach the event stream / terminal / capture trace, and
/// <see cref="SubstitutionRef.SecretDerived"/> is lit up from the secret reference.
/// </summary>
public sealed class SecretRedactionTests
{
    private const string SecretValue = "topsecret-value-xyz";

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(HttpRestProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    // ── helpers ────────────────────────────────────────────────────────────────

    private static ScenarioAst BuildAst(string yaml) =>
        AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

    /// <summary>
    /// Reproduces the runner's per-step provenance + StepCompletedEvent emission for
    /// a single step, returning the JSON Lines string EXACTLY as the runner would
    /// buffer it.  Uses the live <see cref="ScenarioRunner.DeriveSubstitutionProvenance"/>
    /// and <see cref="ScenarioRunner.BuildCaptureOriginMap"/> internals — no topology.
    /// </summary>
    private static string EmitStepCompletedLine(ScenarioAst ast, StepNode node)
    {
        var captureOriginMap = ScenarioRunner.BuildCaptureOriginMap(ast.Steps);
        var substitutions = ScenarioRunner.DeriveSubstitutionProvenance(node, captureOriginMap);

        // Mirror the runner's CapturedVar derivation shape (metadata-only): one entry
        // per declared capture, with Matched left at a fabricated value (the matched
        // flag is irrelevant to the redaction guarantee — there is no value field).
        IReadOnlyList<CapturedVar>? captured = null;
        if (node.Capture.Count > 0)
        {
            captured = node.Capture
                .Select(kv => new CapturedVar(kv.Key, kv.Value, Matched: true))
                .ToList();
        }

        return EventStreamJson.ToLine(new StepCompletedEvent
        {
            RunId = "run-redaction-test",
            Timestamp = DateTimeOffset.UtcNow,
            StepId = node.Id,
            Verdict = Verdict.EnvironmentError,
            DurationMs = 0L,
            Captured = captured,
            Substitutions = substitutions,
        });
    }

    // ── 1. Secret reference field → SecretDerived substitution, no value ─────────

    /// <summary>
    /// An http.rest step whose <c>Authorization</c> header contains
    /// <c>Bearer ${secret:env/&lt;name&gt;}</c> must emit a substitution entry with
    /// <c>secretDerived: true</c> and <c>placeholder == "env/&lt;name&gt;"</c> (the
    /// non-sensitive reference label), and the resolved secret VALUE must appear in
    /// NONE of the buffered event lines (§17).
    /// </summary>
    [Fact]
    public void SecretReferenceField_EmitsSecretDerivedSubstitution()
    {
        // Unique env-var name per run so concurrent tests cannot race the shared
        // process environment (matches the sibling secret pipeline tests).
        var envName = "VOUCHFX_REDACTION_SECRET_" + Guid.NewGuid().ToString("N");
        var referenceLabel = $"env/{envName}";

        // Set the env var to a known value so a careless value-read WOULD leak it.
        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            var yaml =
                $$"""
                environment:
                  services:
                    api:
                      image: traefik/whoami
                steps:
                  - id: call-with-secret
                    type: http.rest
                    target: api
                    method: GET
                    path: /api
                    headers:
                      Authorization: "Bearer ${secret:env/{{envName}}}"
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);
            var node = Assert.Single(ast.Steps);

            // Build the buffer the runner would emit for this step.
            var buffer = new List<string> { EmitStepCompletedLine(ast, node) };

            // ── Parse the StepCompletedEvent and assert the SecretDerived entry ──
            var line = buffer[0];
            var evt = EventStreamJson.FromLine<StepCompletedEvent>(line);

            Assert.NotNull(evt.Substitutions);
            var secretEntry = Assert.Single(evt.Substitutions!, s => s.SecretDerived);

            Assert.True(secretEntry.SecretDerived);
            Assert.Equal(referenceLabel, secretEntry.Placeholder);
            // A secret does not originate from a prior capture step.
            Assert.Null(secretEntry.OriginStepId);

            // The on-the-wire JSON must carry the secretDerived flag and the label,
            // but NEVER the resolved value.
            Assert.Contains("\"secretDerived\":true", line, StringComparison.Ordinal);
            Assert.Contains(referenceLabel, line, StringComparison.Ordinal);

            // ── The resolved VALUE must appear in NONE of the buffered lines ────
            foreach (var bufferedLine in buffer)
            {
                Assert.DoesNotContain(SecretValue, bufferedLine, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 2. Secret value never appears in terminal output ─────────────────────────

    /// <summary>
    /// Rendering the same step's event buffer through <see cref="TerminalRenderer"/>
    /// must never surface the resolved secret value (§17).
    /// </summary>
    [Fact]
    public void SecretValue_NeverAppearsInTerminalOutput()
    {
        var envName = "VOUCHFX_REDACTION_TERM_" + Guid.NewGuid().ToString("N");

        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            var yaml =
                $$"""
                environment:
                  services:
                    api:
                      image: traefik/whoami
                steps:
                  - id: call-with-secret
                    type: http.rest
                    target: api
                    method: GET
                    path: /api
                    headers:
                      Authorization: "Bearer ${secret:env/{{envName}}}"
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);
            var node = Assert.Single(ast.Steps);

            var buffer = new List<string>
            {
                EventStreamJson.ToLine(new ScenarioStartedEvent
                {
                    RunId = "run-redaction-test",
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "secret-redaction",
                }),
                EventStreamJson.ToLine(new StepStartedEvent
                {
                    RunId = "run-redaction-test",
                    Timestamp = DateTimeOffset.UtcNow,
                    StepId = node.Id,
                    Kind = node.CanonicalType,
                    VerifyMode = "IMMEDIATE",
                }),
                EmitStepCompletedLine(ast, node),
                EventStreamJson.ToLine(new ScenarioCompletedEvent
                {
                    RunId = "run-redaction-test",
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "secret-redaction",
                    Verdict = Verdict.EnvironmentError,
                    Counts = new VerdictCounts { EnvError = 1 },
                }),
            };

            var sw = new StringWriter();
            TerminalRenderer.Render(buffer, sw);

            var rendered = sw.ToString();
            Assert.DoesNotContain(SecretValue, rendered, StringComparison.Ordinal);

            // Sanity: the renderer actually produced output for the step.
            Assert.Contains("call-with-secret", rendered, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 3. Regular placeholder is NOT secret-derived ─────────────────────────────

    /// <summary>
    /// A step that uses only a plain <c>{capturedVar}</c> placeholder (no secret
    /// reference) must produce substitution entries with <c>secretDerived: false</c>
    /// — a regular placeholder is never speculatively tainted (§17).
    /// </summary>
    [Fact]
    public void RegularPlaceholder_IsNotSecretDerived()
    {
        // Two steps: the first captures `orderId`, the second substitutes it into a path.
        const string yaml = """
            environment:
              services:
                api:
                  image: traefik/whoami
            steps:
              - id: create-order
                type: http.rest
                target: api
                method: POST
                path: /orders
                capture:
                  orderId: $.id
                expect:
                  status: 200
              - id: fetch-order
                type: http.rest
                target: api
                method: GET
                path: /orders/{orderId}
                expect:
                  status: 200
            """;

        var ast = BuildAst(yaml);
        Assert.Equal(2, ast.Steps.Count);
        var fetchStep = ast.Steps[1];

        var line = EmitStepCompletedLine(ast, fetchStep);
        var evt = EventStreamJson.FromLine<StepCompletedEvent>(line);

        Assert.NotNull(evt.Substitutions);
        var entry = Assert.Single(evt.Substitutions!);

        Assert.Equal("orderId", entry.Placeholder);
        Assert.False(entry.SecretDerived);
        // The placeholder's origin is the prior capture step.
        Assert.Equal("create-order", entry.OriginStepId);

        // None of the entries are secret-derived.
        Assert.DoesNotContain(evt.Substitutions!, s => s.SecretDerived);
        Assert.Contains("\"secretDerived\":false", line, StringComparison.Ordinal);
    }

    // ── 4. Captured-var trace carries name + path only (no value) ────────────────

    /// <summary>
    /// A step with a <c>capture</c> block must emit <see cref="CapturedVar"/> entries
    /// carrying only <c>name</c>, <c>path</c>, and the <c>matched</c> flag — never a
    /// captured value field (§17).
    /// </summary>
    [Fact]
    public void CapturedVarTrace_CarriesNameAndPathOnly()
    {
        const string yaml = """
            environment:
              services:
                api:
                  image: traefik/whoami
            steps:
              - id: create-order
                type: http.rest
                target: api
                method: POST
                path: /orders
                capture:
                  orderId: $.id
                expect:
                  status: 200
            """;

        var ast = BuildAst(yaml);
        var node = Assert.Single(ast.Steps);

        var line = EmitStepCompletedLine(ast, node);
        var evt = EventStreamJson.FromLine<StepCompletedEvent>(line);

        Assert.NotNull(evt.Captured);
        var captured = Assert.Single(evt.Captured!);

        Assert.Equal("orderId", captured.Name);
        Assert.Equal("$.id", captured.Path);

        // Wire shape: name/path/matched only — there is no value field on the record.
        Assert.Contains("\"name\":\"orderId\"", line, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"$.id\"", line, StringComparison.Ordinal);
        Assert.Contains("\"matched\":", line, StringComparison.Ordinal);
        // Defensive: no "value" key anywhere on the captured trace.
        Assert.DoesNotContain("\"value\"", line, StringComparison.Ordinal);
    }

    // ── 5. Mixed step: secret AND plain placeholder co-exist, deduped correctly ──

    /// <summary>
    /// A step whose substitutable fields contain BOTH a plain <c>{placeholder}</c>
    /// and a <c>${secret:…}</c> reference must emit exactly one
    /// <c>secretDerived: false</c> entry for the placeholder and exactly one
    /// <c>secretDerived: true</c> entry for the secret reference — the two grammars
    /// never collide (§17, S05-B-01).
    /// </summary>
    [Fact]
    public void MixedSecretAndPlaceholder_ProducesBothEntryKinds()
    {
        var envName = "VOUCHFX_REDACTION_MIX_" + Guid.NewGuid().ToString("N");
        var referenceLabel = $"env/{envName}";

        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            var yaml =
                $$"""
                environment:
                  services:
                    api:
                      image: traefik/whoami
                variables:
                  region: eu-west
                steps:
                  - id: call-mixed
                    type: http.rest
                    target: api
                    method: GET
                    path: /api/{region}
                    headers:
                      Authorization: "Bearer ${secret:env/{{envName}}}"
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);
            var node = Assert.Single(ast.Steps);

            var line = EmitStepCompletedLine(ast, node);
            var evt = EventStreamJson.FromLine<StepCompletedEvent>(line);

            Assert.NotNull(evt.Substitutions);

            // Exactly one plain placeholder (region) — not secret-derived.
            var plain = Assert.Single(evt.Substitutions!, s => !s.SecretDerived);
            Assert.Equal("region", plain.Placeholder);
            Assert.False(plain.SecretDerived);

            // Exactly one secret reference — secret-derived, label only.
            var secret = Assert.Single(evt.Substitutions!, s => s.SecretDerived);
            Assert.Equal(referenceLabel, secret.Placeholder);
            Assert.Null(secret.OriginStepId);

            // The value is nowhere in the emitted line.
            Assert.DoesNotContain(SecretValue, line, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }
}
