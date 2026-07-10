// S05-B-02 — THE CRITICAL IL TEST (non-docker).
//
// The non-negotiable invariant (§17): no secret VALUE is ever baked into the
// compiled IL. Providers emit the reference TOKEN as a literal plus a runtime
// Secret_Helpers.Resolve(Secrets, …) call; the value is read only at execution time.
//
// This test compiles a scenario whose http.rest step carries a header
// `Authorization: Bearer ${secret:env/API_TOKEN}` and asserts:
//   • the assembled CSX SOURCE contains the literal "${secret:env/API_TOKEN}"
//     but NOT the secret value "topsecret";
//   • the emitted IL bytes (the MemoryStream image from RoslynScriptCompiler)
//     likewise contain the reference token but NOT the secret value.
//
// Plus MissingSecret_YieldsEnvironmentError: at the helper level, a
// SecretResolutionException raised inside ExecuteAsync is caught and written as a
// per-step EnvironmentError StepOutcome (no live HTTP endpoint required; full M2
// proof lives in the docker scenario S05-D-01).

using System;
using System.Collections.Generic;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Proves the no-IL-baking invariant (§17) over the full compile pipeline and that a
/// missing secret maps to a per-step <see cref="Verdict.EnvironmentError"/> (§12.1).
/// </summary>
public sealed class SecretResolutionPipelineTests
{
    private const string SuiteNamespace = "VouchfxGenerated";
    private const string SecretValue = "topsecret";

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(HttpRestProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    // A scenario whose http.rest step sends an Authorization header carrying a secret
    // reference. The header value is a mixed literal + secret token. The env-var name
    // is parameterised with a per-test GUID so concurrent test runs cannot collide on
    // the shared process environment (matches EnvironmentSecretResolverTests).
    private static string BuildSecretHeaderYaml(string envName) =>
        $$"""
        metadata:
          name: secret-header

        environment:
          services:
            api:
              image: traefik/whoami
              httpPort: 80

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

    /// <summary>
    /// THE CRITICAL IL TEST: with the env var set, neither the assembled CSX source
    /// nor the emitted IL image may contain the secret value; both must contain the
    /// reference token verbatim (§17).
    /// </summary>
    [Fact]
    public void SecretReference_IsEmittedAsToken_NeverBakedIntoSourceOrIl()
    {
        // Unique env-var name per test run so the shared process environment cannot be
        // raced by the sibling MissingSecret test (or any other test) under parallelism.
        var envName = "VOUCHFX_PIPELINE_SECRET_" + Guid.NewGuid().ToString("N");
        var referenceToken = $"${{secret:env/{envName}}}";

        // Set the env var so a careless compile-time read WOULD leak the value into IL.
        // The whole point is that it must NOT be read at compile time.
        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            // ── Compile the scenario through the provider pipeline ─────────────
            var doc = YamlDocumentParser.Parse(BuildSecretHeaderYaml(envName));
            var ast = AstBuilder.Build(doc, s_registry);

            var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);
            Assert.Null(result.Failure);
            Assert.NotNull(result.Assembled);

            var src = result.Assembled!.CsxSource;

            // ── Assert on the assembled SOURCE ─────────────────────────────────
            Assert.Contains(referenceToken, src, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, src, StringComparison.Ordinal);

            // The provider must plumb the secret/substitute helpers + the accessor.
            Assert.Contains("Secret_Helpers", src, StringComparison.Ordinal);
            Assert.Contains("Substitute_Helpers", src, StringComparison.Ordinal);
            Assert.Contains("Secrets", src, StringComparison.Ordinal);

            // ── Assert on the emitted IL BYTES ─────────────────────────────────
            // Supply both the pipeline's provider refs (Npgsql / JsonPath.Net) and the
            // BCL refs the HttpRest_Helpers body needs (System.Private.Uri,
            // System.Text.Json, …) — the runner does the same concatenation.
            var refs = new List<string>(result.CompileReferencePaths);
            refs.AddRange(s_executionRefs);

            var compiled = RoslynScriptCompiler.CompileOnce(
                src,
                additionalReferencePaths: refs);

            // .NET stores managed string literals UTF-16-encoded in the #US (user
            // string) metadata heap.  The reference token IS present there (it is a
            // string literal the helper passes to Secret_Helpers.Resolve at runtime).
            Assert.True(ContainsBytes(compiled.Image, Encoding.Unicode.GetBytes(referenceToken)),
                "The reference token must be present in the emitted IL as a UTF-16 literal.");

            // The secret VALUE must appear NOWHERE in the IL — neither as a UTF-16
            // metadata string literal nor as any UTF-8 byte run.  This is the
            // load-bearing no-IL-baking assertion (§17).
            Assert.False(ContainsBytes(compiled.Image, Encoding.Unicode.GetBytes(SecretValue)),
                "The secret value must not appear as a UTF-16 string in the emitted IL.");
            Assert.False(ContainsBytes(compiled.Image, Encoding.UTF8.GetBytes(SecretValue)),
                "The secret value must not appear as a UTF-8 string in the emitted IL.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    /// <summary>
    /// A missing secret raised inside the http.rest helper's guarded region must be
    /// caught and written as a per-step <see cref="Verdict.EnvironmentError"/> — it must
    /// neither escape the step (scenario abort) nor be recorded as a Fail (§12.1).
    /// The full live-HTTP proof lives in the docker scenario S05-D-01.
    /// </summary>
    [Fact]
    public async Task MissingSecret_YieldsEnvironmentError()
    {
        // Unique name so the variable is guaranteed absent and cannot be raced by a
        // sibling test that sets a fixed name (matches EnvironmentSecretResolverTests).
        var envName = "VOUCHFX_PIPELINE_MISSING_" + Guid.NewGuid().ToString("N");

        // Ensure the variable is absent (defensive; the GUID name should not exist).
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "api",
                Method: "GET",
                Path: "/api",
                Headers: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = $"Bearer ${{secret:env/{envName}}}",
                },
                Body: null,
                Expect: new HttpExpect(Status: 200));

            const string stepId = "call-with-secret";
            var fragment = provider.Emit(model, new StubCompileContext(stepId));

            var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource,
                additionalReferencePaths: s_executionRefs);

            // Real env-backed accessor; the variable is unset so resolution will throw
            // SecretResolutionException INSIDE the helper's guarded region.
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // Seed a base URL so the failure is the secret, not a missing service.
                [VarKeys.Service("api")] = "http://localhost:1",
            };
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            // Must NOT throw — the exception is contained inside the step.
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId(stepId);
            var outcomeKey = VarKeys.Outcome(safeId);
            Assert.True(vars.ContainsKey(outcomeKey),
                $"Vars must contain outcome key '{outcomeKey}'.");

            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);

            // The observation names the source/path coordinates, never the value.
            Assert.NotNull(outcome.Observation);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> s_executionRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,          // System.Private.Xml — XPath capture logic (S07-B-01b)
    };

    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
