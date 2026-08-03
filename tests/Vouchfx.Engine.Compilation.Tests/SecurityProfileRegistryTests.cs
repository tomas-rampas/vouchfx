// SecurityProfileRegistry tests (authenticated-infrastructure-mtls, slice C — REQ-022).
//
// Two things this file proves:
//   1. The registry itself behaves like StepKindRegistry's own frozen-at-startup discovery
//      (BuiltIn discovers exactly the two built-in wirings; TryGet/TryResolve behave per their
//      own contract; a duplicate profile registration is rejected).
//   2. REQ-022's own acceptance criterion, empirically: every (profile, target-kind) pair the
//      REAL composed schema permits resolves to a registered wiring in SecurityProfileRegistry.BuiltIn
//      — derived from actual schema validation (DocumentValidator), not a hand-maintained
//      parallel list that could silently drift from the schema's own REQ-021 narrowing.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class SecurityProfileRegistryTests
{
    // ── The built-in registry ────────────────────────────────────────────────────

    [Fact]
    public void BuiltIn_RegistersExactlyTlsAndMtls()
    {
        var profiles = SecurityProfileRegistry.BuiltIn.All
            .Select(w => w.Profile)
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToList();

        Assert.Equal(s_expectedBuiltInProfiles, profiles);
    }

    private static readonly string[] s_expectedBuiltInProfiles = { "mtls", "tls" };

    [Fact]
    public void TryGet_RegisteredProfile_ReturnsTrue()
    {
        Assert.True(SecurityProfileRegistry.BuiltIn.TryGet("tls", out var wiring));
        Assert.NotNull(wiring);
        Assert.Equal("tls", wiring!.Profile);
    }

    [Fact]
    public void TryGet_UnregisteredProfile_ReturnsFalse()
    {
        Assert.False(SecurityProfileRegistry.BuiltIn.TryGet("acme-sasl", out var wiring));
        Assert.Null(wiring);
    }

    [Fact]
    public void TryResolve_TlsOnAnyKind_Resolves()
    {
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve("tls", "redis", out _));
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve("tls", "kafka", out _));
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve("tls", SecurityProfileRegistry.ServiceTargetKind, out _));
    }

    [Fact]
    public void TryResolve_MtlsOnKafkaOrService_Resolves()
    {
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve("mtls", "kafka", out _));
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve("mtls", SecurityProfileRegistry.ServiceTargetKind, out _));
    }

    [Fact]
    public void TryResolve_MtlsOnNonKafkaDependency_DoesNotResolve()
    {
        Assert.False(SecurityProfileRegistry.BuiltIn.TryResolve("mtls", "redis", out var wiring));
        Assert.Null(wiring);
    }

    [Fact]
    public void BuildAndFreeze_DuplicateProfile_Throws()
    {
        var duplicate = new[]
        {
            new StubWiring("tls"),
            new StubWiring("tls"),
        };

        Assert.Throws<DuplicateSecurityProfileException>(
            () => SecurityProfileRegistry.BuildAndFreeze(duplicate));
    }

    /// <summary>
    /// REQ-022's own acceptance: "removing a wiring must make a suite declaring that pair
    /// fail" — proven here directly against the registry (the pipeline-level proof, through
    /// <c>SecurityProfileWiringValidator</c> and <c>ProviderPipeline.Compile</c>, lives in
    /// <c>Vouchfx.Engine.Runtime.Tests.SecurityProfileWiringValidatorTests</c>). Builds a
    /// REDUCED registry from <see cref="SecurityProfileRegistry.BuiltIn"/>'s own wirings minus
    /// 'mtls' — never a hand-authored stand-in — so the removed pair is proven to have
    /// resolved a moment ago and now does not.
    /// </summary>
    [Fact]
    public void RemovingAWiring_MakesItsPairsFailToResolve()
    {
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve("mtls", "kafka", out _),
            "Precondition: 'mtls' on 'kafka' must resolve against the FULL built-in registry.");

        var reduced = SecurityProfileRegistry.BuildAndFreeze(
            SecurityProfileRegistry.BuiltIn.All
                .Where(w => w.Profile != "mtls")
                .Select(w => w.Instance)
                .ToList());

        Assert.False(reduced.TryResolve("mtls", "kafka", out var wiring));
        Assert.Null(wiring);
        // 'tls' is untouched by removing 'mtls' — the reduction is scoped to the one profile.
        Assert.True(reduced.TryResolve("tls", "kafka", out _));
    }

    // ── REQ-022: every (profile, kind) pair the schema permits resolves to a wiring ──

    private static StepKindRegistry StepRegistry() =>
        StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

    private static readonly string[] s_dependencyKinds =
    {
        "postgres", "sqlserver", "mysql", "mongodb", "redis", "elasticsearch",
        "rabbitmq", "nats", "kafka", "mailpit", "azureservicebus", "dynamodb", "minio",
    };

    /// <summary>
    /// Every <c>(profile, kind)</c> pair the schema permits — derived from ACTUAL schema
    /// validation via <see cref="DocumentValidator"/> against every one of the thirteen
    /// dependency kinds plus the service sentinel, both profiles — resolves to a registered
    /// wiring in <see cref="SecurityProfileRegistry.BuiltIn"/>. This is the enumeration
    /// REQ-022's acceptance criterion names, built from the schema itself rather than a
    /// hand-maintained parallel list that could silently drift from REQ-021's own narrowing.
    /// </summary>
    public static IEnumerable<object[]> DependencyKindAndProfileCombinations()
    {
        foreach (var kind in s_dependencyKinds)
        {
            yield return new object[] { kind, "tls" };
            yield return new object[] { kind, "mtls" };
        }
    }

    /// <summary>
    /// Builds a minimal document declaring one dependency of <paramref name="dependencyKind"/>
    /// with a <c>security.profile</c> of <paramref name="profile"/> — shared by every theory
    /// AND the coverage-floor fact below (G-MAJOR-2) so the fixture shape lives in exactly one
    /// place.
    /// </summary>
    private static string BuildDependencyYaml(string dependencyKind, string profile)
    {
        var extraFields = profile == "mtls"
            ? "\n        clientCert: ./certs/client.pem\n        clientKey: ./certs/client-key.pem"
            : string.Empty;

        return $$"""
            environment:
              dependencies:
                dep:
                  type: {{dependencyKind}}
                  security:
                    profile: {{profile}}
                    endpoint: 9999{{extraFields}}
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;
    }

    /// <summary>
    /// Builds a minimal document declaring one service with a <c>security.profile</c> of
    /// <paramref name="profile"/> — the service-side sibling of <see cref="BuildDependencyYaml"/>.
    /// </summary>
    private static string BuildServiceYaml(string profile)
    {
        var extraFields = profile == "mtls"
            ? "\n        clientCert: ./certs/client.pem\n        clientKey: ./certs/client-key.pem"
            : string.Empty;

        return $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: {{profile}}
                    endpoint: 9999{{extraFields}}
            steps:
              - id: noop
                type: script.csharp
                code: "// noop"
            """;
    }

    [Theory]
    [MemberData(nameof(DependencyKindAndProfileCombinations))]
    public void EveryDependencyKindProfilePair_PermittedBySchema_ResolvesToRegisteredWiring(
        string dependencyKind, string profile)
    {
        var result = DocumentValidator.Validate(BuildDependencyYaml(dependencyKind, profile), StepRegistry());

        if (!result.IsValid)
        {
            // Not permitted by the schema for this (kind, profile) pair (REQ-021's own
            // narrowing) — nothing for the registry to resolve; not this test's concern.
            return;
        }

        Assert.True(
            SecurityProfileRegistry.BuiltIn.TryResolve(profile, dependencyKind, out _),
            $"Schema PERMITS profile '{profile}' on dependency kind '{dependencyKind}', but " +
            "no registered wiring resolves it — REQ-022's invariant would be silently unmet.");
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("mtls")]
    public void EveryServiceProfile_PermittedBySchema_ResolvesToRegisteredWiring(string profile)
    {
        var result = DocumentValidator.Validate(BuildServiceYaml(profile), StepRegistry());

        if (!result.IsValid)
        {
            return;
        }

        Assert.True(
            SecurityProfileRegistry.BuiltIn.TryResolve(profile, SecurityProfileRegistry.ServiceTargetKind, out _),
            $"Schema PERMITS profile '{profile}' on a declared service, but no registered " +
            "wiring resolves it — REQ-022's invariant would be silently unmet.");
    }

    /// <summary>
    /// G-MAJOR-2 (gatekeeper): the missing coverage FLOOR. Both theories above silently
    /// <c>return</c> (asserting nothing) whenever the schema rejects a given combination — so a
    /// template typo that made EVERY document invalid would leave all 28 theory cases green
    /// without ever exercising a single registry resolution, the exact "vacuously passes"
    /// failure mode this file otherwise guards against. This walks the SAME 26
    /// dependency-kind/profile combinations plus the 2 service/profile combinations directly
    /// and asserts the EXACT count the schema permits — measured: 16 (<c>tls</c> on all 13
    /// dependency kinds, <c>mtls</c> on kafka only, both profiles on a service) — so a
    /// validator regression that silently starts accepting or rejecting a combination it
    /// should not is caught here even if every theory above degenerated to a no-op.
    /// </summary>
    [Fact]
    public void PermittedCombinationCount_AcrossAllDependencyKindsAndServiceProfiles_IsExactlySixteen()
    {
        var registry = StepRegistry();
        var permittedCount = 0;

        foreach (var kind in s_dependencyKinds)
        {
            foreach (var profile in new[] { "tls", "mtls" })
            {
                if (DocumentValidator.Validate(BuildDependencyYaml(kind, profile), registry).IsValid)
                {
                    permittedCount++;
                }
            }
        }

        foreach (var profile in new[] { "tls", "mtls" })
        {
            if (DocumentValidator.Validate(BuildServiceYaml(profile), registry).IsValid)
            {
                permittedCount++;
            }
        }

        Assert.Equal(16, permittedCount);
    }

    /// <summary>
    /// Guards the enumeration itself: every dependency kind the SCHEMA's own <c>type</c>
    /// enum recognises must be exercised above — a kind silently missing from
    /// <see cref="s_dependencyKinds"/> would just never run its own theory case, the exact
    /// "vacuously passes" failure mode <c>SchemaStepSurfaceClosureTests</c>'
    /// <c>MinimalValidStepDocuments_CoversExactlyTheRegisteredCoreProviders</c> guards against
    /// for step types.
    /// </summary>
    /// <remarks>
    /// G-MAJOR-2 (gatekeeper): previously compared <c>s_dependencyKinds.Length</c> against the
    /// hardcoded literal <c>13</c> — a fourteenth kind added to BOTH the schema and this array
    /// would leave <c>Length == 13</c> false and fail loudly, but a fourteenth kind added to the
    /// SCHEMA alone (the actual regression this guard exists to catch) left <c>Length</c>
    /// unchanged at 13 and the guard green, the new kind never exercised by any theory above —
    /// precisely REQ-021's own allow-list scenario. Fixed by deriving the expected set from the
    /// LIVE composed schema's own <c>$defs/dependency.properties.type.enum</c> and asserting SET
    /// equality against this file's own array — following the exemplar this docstring already
    /// cites, <c>SchemaStepSurfaceClosureTests.MinimalValidStepDocuments_CoversExactlyTheRegisteredCoreProviders</c>,
    /// which does the identical real-registry-vs-hardcoded-fixture comparison for step types.
    /// </remarks>
    [Fact]
    public void DependencyKindsEnumerated_MatchesSchemasOwnTypeEnum()
    {
        var composedSchemaJson = SchemaComposer.ComposeSchemaJson(StepRegistry());
        using var schemaDocument = JsonDocument.Parse(composedSchemaJson);

        var expectedKinds = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("dependency")
            .GetProperty("properties")
            .GetProperty("type")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToList();

        var actualKinds = s_dependencyKinds
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedKinds, actualKinds);
        Assert.Equal(s_dependencyKinds.Length, s_dependencyKinds.Distinct().Count());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private sealed class StubWiring : ISecurityProfileWiring
    {
        public StubWiring(string profile) => Profile = profile;

        public string Profile { get; }

        public bool AppliesTo(string targetKind) => true;
    }
}
