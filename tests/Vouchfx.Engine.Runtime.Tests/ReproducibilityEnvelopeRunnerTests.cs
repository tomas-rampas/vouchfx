// S05-B-03 — reproducibility envelope assembled by the runner (non-docker).
//
// Proves the runner builds the envelope the way the brief specifies: the hash of
// every distinct secret REFERENCE across the steps' substitutable fields, plus the
// content hash of every applied seed fixture — with provably no resolved secret
// value (§17, docs/02 §3.2.2).
//
// Why these tests do NOT stand up a topology (mirrors SecretRedactionTests):
//   The runner emits the reproducibility-envelope line only AFTER the isolated
//   Roslyn delegate has run against a live topology, which needs Docker.  Per the
//   non-docker pattern already used for provenance/redaction, we exercise the exact
//   internal the runner uses — ScenarioRunner.BuildReproducibilityEnvelope — fed by
//   real StepNodes built from YAML via the live parser + AstBuilder, and a real seed
//   SQL fixture written to a temp directory.  We then wrap the envelope in the same
//   ReproducibilityEnvelopeEvent via the same EventStreamJson.ToLine the runner uses
//   and assert on the emitted line.  The full live end-to-end emission is the
//   Docker M2 scenario.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Non-docker tests for the S05-B-03 reproducibility envelope assembly performed by
/// <see cref="ScenarioRunner.BuildReproducibilityEnvelope"/>.
/// </summary>
public sealed class ReproducibilityEnvelopeRunnerTests
{
    private const string SecretValue = "topsecret-runner-value-9c2e";
    private const string FixtureSqlBody =
        "INSERT INTO orders (id, status) VALUES (1, 'PENDING');\n";

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(HttpRestProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private static ScenarioAst BuildAst(string yaml) =>
        AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

    private static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    private static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Reproduces the runner's per-scenario envelope emission: build the envelope
    /// via the internal, wrap it in <see cref="ReproducibilityEnvelopeEvent"/> via
    /// the shared <see cref="EventStreamJson.ToLine{T}"/> — exactly as the runner
    /// buffers it.
    /// </summary>
    private static string EmitEnvelopeLine(ScenarioAst ast, string? seedBaseDirectory)
    {
        var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, seedBaseDirectory);
        return EventStreamJson.ToLine(new ReproducibilityEnvelopeEvent
        {
            RunId = "run-repro-runner",
            Timestamp = DateTimeOffset.UtcNow,
            ScenarioId = "repro-runner-scenario",
            EnvSchemaVersion = envelope.SchemaVersion,
            SecretReferences = envelope.SecretReferences,
            Fixtures = envelope.Fixtures,
        });
    }

    // -------------------------------------------------------------------------
    // 1. Secret reference hash + fixture content hash present; value absent.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A scenario with a <c>${secret:env/X}</c> header (env set to a known value)
    /// AND a seed SQL fixture on disk must emit a <c>reproducibility-envelope</c>
    /// line that contains the reference hash and the fixture content hash, and NOT
    /// the secret value nor the fixture's raw body (§17, docs/02 §3.2.2).
    /// </summary>
    [Fact]
    public void Envelope_ContainsReferenceHashAndFixtureHash_NotSecretValue()
    {
        var envName = "VOUCHFX_REPRO_RUNNER_" + Guid.NewGuid().ToString("N");
        var rawToken = $"${{secret:env/{envName}}}";

        // A temp seed base directory with a real SQL fixture file.
        var baseDir = Path.Combine(
            Path.GetTempPath(), "vouchfx-repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var fixturesDir = Path.Combine(baseDir, "fixtures");
        Directory.CreateDirectory(fixturesDir);
        var sqlRelative = "fixtures/orders.sql";
        File.WriteAllText(Path.Combine(baseDir, sqlRelative), FixtureSqlBody);

        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            var yaml =
                $$"""
                environment:
                  services:
                    api:
                      image: traefik/whoami
                  dependencies:
                    orders-db:
                      type: postgres
                  seed:
                    orders-db:
                      sql: [ "fixtures/orders.sql" ]
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
            var line = EmitEnvelopeLine(ast, baseDir);

            var evt = EventStreamJson.FromLine<ReproducibilityEnvelopeEvent>(line);
            Assert.Equal(EventTypes.ReproducibilityEnvelope, evt.Type);
            Assert.Equal("v1", evt.EnvSchemaVersion);

            // ── Secret reference: hash present, value absent. ──────────────────
            var secretDigest = Assert.Single(evt.SecretReferences);
            Assert.Equal("env", secretDigest.Source);
            Assert.Equal(Sha256Hex(rawToken), secretDigest.ReferenceHash);
            Assert.Contains(Sha256Hex(rawToken), line, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, line, StringComparison.Ordinal);

            // ── Fixture: content hash present, raw body absent. ────────────────
            var fixtureDigest = Assert.Single(evt.Fixtures);
            Assert.Equal(sqlRelative, fixtureDigest.Reference);
            Assert.Equal(Sha256Hex(FixtureSqlBody), fixtureDigest.ContentHash);
            Assert.Contains(Sha256Hex(FixtureSqlBody), line, StringComparison.Ordinal);
            // The raw SQL contents (as a value) must never be in the envelope.
            Assert.DoesNotContain("INSERT INTO orders", line, StringComparison.Ordinal);
            Assert.DoesNotContain("PENDING", line, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
            try { Directory.Delete(baseDir, recursive: true); } catch (IOException) { }
        }
    }

    // -------------------------------------------------------------------------
    // 2. Fixture hash equals the seed applier's hash (shared routine, no dupe).
    // -------------------------------------------------------------------------

    /// <summary>
    /// The fixture content hash recorded in the envelope must equal the lower-case
    /// hex SHA-256 of the file's raw bytes — i.e. the same value
    /// <c>SeedFixtures.ComputeContentHash</c> produces (S05-A-02), proving the
    /// envelope reuses the shared routine rather than reimplementing it.
    /// </summary>
    [Fact]
    public void FixtureHash_MatchesRawFileByteHash()
    {
        var baseDir = Path.Combine(
            Path.GetTempPath(), "vouchfx-repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var fixturesDir = Path.Combine(baseDir, "fixtures");
        Directory.CreateDirectory(fixturesDir);
        var sqlRelative = "fixtures/seed.sql";
        var fullPath = Path.Combine(baseDir, sqlRelative);
        File.WriteAllText(fullPath, FixtureSqlBody);

        try
        {
            var yaml = """
                environment:
                  dependencies:
                    orders-db:
                      type: postgres
                  seed:
                    orders-db:
                      sql: [ "fixtures/seed.sql" ]
                steps:
                  - id: noop
                    type: http.rest
                    target: api
                    method: GET
                    path: /api
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);
            var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, baseDir);

            var fixture = Assert.Single(envelope.Fixtures);
            var expected = Sha256Hex(File.ReadAllBytes(fullPath));
            Assert.Equal(expected, fixture.ContentHash);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch (IOException) { }
        }
    }

    // -------------------------------------------------------------------------
    // 3. Missing fixture file → recorded with null hash, no throw.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A seed fixture whose file is absent at envelope-build time must be recorded
    /// with a <see langword="null"/> content hash rather than throwing — the
    /// envelope must never crash a run (the missing file is already an Environment
    /// error in the seed applier, §12.1).
    /// </summary>
    [Fact]
    public void MissingFixtureFile_RecordedWithNullHash_DoesNotThrow()
    {
        var baseDir = Path.Combine(
            Path.GetTempPath(), "vouchfx-repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);

        try
        {
            var yaml = """
                environment:
                  dependencies:
                    orders-db:
                      type: postgres
                  seed:
                    orders-db:
                      sql: [ "fixtures/does-not-exist.sql" ]
                steps:
                  - id: noop
                    type: http.rest
                    target: api
                    method: GET
                    path: /api
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);

            // Must not throw.
            var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, baseDir);

            var fixture = Assert.Single(envelope.Fixtures);
            Assert.Equal("fixtures/does-not-exist.sql", fixture.Reference);
            Assert.Null(fixture.ContentHash);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch (IOException) { }
        }
    }

    // -------------------------------------------------------------------------
    // 4. No seed, no secrets → empty arrays, still emits a valid envelope.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A scenario with neither secret references nor a seed block must still produce
    /// a well-formed envelope with empty arrays (the receipt is always emitted).
    /// </summary>
    [Fact]
    public void NoSecretsNoSeed_EmptyArrays()
    {
        const string yaml = """
            environment:
              services:
                api:
                  image: traefik/whoami
            steps:
              - id: plain
                type: http.rest
                target: api
                method: GET
                path: /api
                expect:
                  status: 200
            """;

        var ast = BuildAst(yaml);
        var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, seedBaseDirectory: null);

        Assert.Empty(envelope.SecretReferences);
        Assert.Empty(envelope.Fixtures);
        Assert.Equal("v1", envelope.SchemaVersion);

        // The emitted line is valid and round-trips.
        var line = EmitEnvelopeLine(ast, seedBaseDirectory: null);
        var evt = EventStreamJson.FromLine<ReproducibilityEnvelopeEvent>(line);
        Assert.Empty(evt.SecretReferences);
        Assert.Empty(evt.Fixtures);
    }

    // -------------------------------------------------------------------------
    // 5. The same reference in two fields → one digest (dedupe through the runner).
    // -------------------------------------------------------------------------

    /// <summary>
    /// When the same secret reference appears in two substitutable fields (the path
    /// and a header), the envelope records exactly one digest for it (dedupe by the
    /// verbatim token).
    /// </summary>
    [Fact]
    public void SameSecretInTwoFields_OneDigest()
    {
        var envName = "VOUCHFX_REPRO_DEDUPE_" + Guid.NewGuid().ToString("N");
        var rawToken = $"${{secret:env/{envName}}}";

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
                  - id: call-twice
                    type: http.rest
                    target: api
                    method: GET
                    path: "/api/${secret:env/{{envName}}}"
                    headers:
                      Authorization: "Bearer ${secret:env/{{envName}}}"
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);
            var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, seedBaseDirectory: null);

            var digest = Assert.Single(envelope.SecretReferences);
            Assert.Equal(Sha256Hex(rawToken), digest.ReferenceHash);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }
}
