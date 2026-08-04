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
using System.IO;
using System.Linq;
using System.Reflection;
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

    /// <summary>
    /// Every profile this registry wires FOR THE TARGET KINDS A GIVEN EMITTED HELPER CAN SERVE is
    /// named explicitly in that helper's profile switch, and no other — the guard that stops a
    /// future wired profile from silently inheriting transport semantics nobody chose for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are two genuinely separate decisions and both have to be made. Registering an
    /// <c>ISecurityProfileWiring</c> makes a profile DECLARABLE; it says nothing about what a step
    /// should do when it meets one. <c>Security_Helpers.ConfigureHandler</c> (HTTP family) and
    /// <c>KafkaSecurity_Helpers.ConfigureClient</c> (Kafka family) answer that second question,
    /// and both fail closed — a profile they do not name throws rather than falling through to
    /// "present whatever certificates happen to be non-null".
    /// </para>
    /// <para>
    /// Failing closed is what makes the drift SAFE, and this test is what makes it VISIBLE.
    /// Without it, adding a wiring and forgetting the helper produces a profile that validates at
    /// authoring time and then errors at step-execution time — correct, but discovered by an
    /// author rather than by the person who added the profile. The assertion is on the exact SET
    /// in both directions, so removing a profile from a helper while it is still wired fails too.
    /// </para>
    /// <para>
    /// <strong>SCOPED BY TARGET KIND (issue #362, fixed in slice E).</strong> This guard used to
    /// compare against the UNFILTERED registry, which was correct only while every wiring covered
    /// every wired kind. It stopped being merely theoretical the moment a second helper existed:
    /// a profile wired for <c>kafka</c> alone would have demanded a DEAD arm in the HTTP helper —
    /// an arm no HTTP step could ever reach, since <c>http.rest</c>/<c>http.soap</c>/
    /// <c>metrics-assert.prometheus</c> resolve <c>target</c> exclusively through
    /// <c>DeclaredServices</c> — and the only way to satisfy it would be to write Kafka semantics
    /// into an HTTP handler. Each helper is therefore held to the profiles wired for the kinds its
    /// own providers can actually target, which is what the guard always meant.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EmittedSecurityHelpers))]
    public void EveryWiredProfile_IsNamedInTheEmittedHelpersProfileSwitch(
        string helperName, string helperSource, string[] servedTargetKinds)
    {
        // The helper's switch arms, read out of the emitted source rather than restated: a
        // second hand-maintained list here would be the very drift this test exists to catch.
        var named = System.Text.RegularExpressions.Regex
            .Matches(helperSource, @"string\.Equals\(profile, ""(?<profile>[^""]+)""")
            .Select(m => m.Groups["profile"].Value)
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToList();

        // Guards the regex itself: a rewrite that changed the comparison shape would otherwise
        // report an empty set and compare equal to an empty registry.
        Assert.NotEmpty(named);

        // Wired FOR THIS HELPER'S OWN REACHABLE KINDS — resolved through the registry's own
        // TryResolve rather than by reading AppliesTo directly, so this asks exactly the question
        // SecurityProfileWiringValidator asks when it decides whether a suite validates.
        var wired = SecurityProfileRegistry.BuiltIn.All
            .Where(w => servedTargetKinds.Any(
                kind => SecurityProfileRegistry.BuiltIn.TryResolve(w.Profile, kind, out _)))
            .Select(w => w.Profile)
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToList();

        Assert.Equal(wired, named);
        Assert.NotEmpty(helperName);
    }

    /// <summary>
    /// The emitted helpers that carry a profile switch, each with the target kinds its own
    /// providers can actually resolve a <c>target</c> to.
    /// </summary>
    /// <remarks>
    /// <c>Security_Helpers</c> serves the three HTTP-family providers, which resolve <c>target</c>
    /// only through <c>IProjectContext.DeclaredServices</c> — a dependency target is rejected
    /// outright (REQ-012 as narrowed), so no dependency kind is reachable from it.
    /// <c>KafkaSecurity_Helpers</c> serves <c>mq-publish.kafka</c>/<c>mq-expect.kafka</c>, which
    /// accept a <c>kafka</c> dependency OR a declared service (REQ-011), so both kinds are listed
    /// — and since REQ-023's amendment both are reachable at run time too: a service target is
    /// staged as the bootstrap authority those clients consume and both providers emit the
    /// <c>svc::</c> key for it.
    /// </remarks>
    public static TheoryData<string, string, string[]> EmittedSecurityHelpers() => new()
    {
        {
            "Security_Helpers",
            SecurityHelper.Source,
            new[] { SecurityProfileRegistry.ServiceTargetKind }
        },
        {
            "KafkaSecurity_Helpers",
            KafkaSecurityHelper.Source,
            new[] { "kafka", SecurityProfileRegistry.ServiceTargetKind }
        },
    };

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

    /// <summary>
    /// M1 (peer review, fix round 2): BOTH built-in profiles are wired for exactly the same two
    /// target kinds — a kafka dependency and any declared service. <c>tls</c> used to return
    /// <see langword="true"/> for every kind unconditionally; that made
    /// <c>TryResolve("tls", "totally-bogus-kind")</c> true and, worse, let a suite validate
    /// <c>profile: tls</c> on (say) a postgres dependency for which nothing stages a TLS client
    /// connection at all. Since <c>AppliesTo</c> gates which suites VALIDATE, that could only be
    /// tightened by rejecting suites that had validated before — so it is tightened now, before
    /// 1.0 freezes it, and widens in 1.1 as REQ-013 lands.
    /// </summary>
    [Theory]
    [InlineData("tls")]
    [InlineData("mtls")]
    public void TryResolve_OnAWiredTargetKind_Resolves(string profile)
    {
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve(profile, "kafka", out _));
        Assert.True(SecurityProfileRegistry.BuiltIn.TryResolve(
            profile, SecurityProfileRegistry.ServiceTargetKind, out _));
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("mtls")]
    public void TryResolve_OnAnUnwiredDependencyKind_DoesNotResolve(string profile)
    {
        Assert.False(SecurityProfileRegistry.BuiltIn.TryResolve(profile, "redis", out var wiring));
        Assert.Null(wiring);
    }

    /// <summary>
    /// The registry answers "no" for a target kind that does not exist at all, for either
    /// profile — the critic's own measurement of the old shape was that
    /// <c>TryResolve("tls", "totally-bogus-kind")</c> returned <see langword="true"/>.
    /// </summary>
    [Theory]
    [InlineData("tls")]
    [InlineData("mtls")]
    public void TryResolve_OnANonExistentTargetKind_DoesNotResolve(string profile)
    {
        Assert.False(SecurityProfileRegistry.BuiltIn.TryResolve(profile, "totally-bogus-kind", out _));
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
    /// The four <c>(target, profile)</c> pairs the composed schema permits at 1.0 — ordinally
    /// sorted, to match the assertion's own ordering.
    /// </summary>
    private static readonly string[] s_expectedPermittedCombinations =
    {
        "dependency:kafka/mtls",
        "dependency:kafka/tls",
        "service/mtls",
        "service/tls",
    };

    /// <summary>
    /// G-MAJOR-2 (gatekeeper): the missing coverage FLOOR. Both theories above silently
    /// <c>return</c> (asserting nothing) whenever the schema rejects a given combination — so a
    /// template typo that made EVERY document invalid would leave all 28 theory cases green
    /// without ever exercising a single registry resolution, the exact "vacuously passes"
    /// failure mode this file otherwise guards against. This walks the SAME 26
    /// dependency-kind/profile combinations (the thirteen <see cref="s_dependencyKinds"/> entries
    /// × two profiles — the ENUMERATED space, not the permitted subset) plus the 2
    /// service/profile combinations directly and asserts EXACTLY WHICH ONES the schema permits,
    /// so a validator regression that silently starts accepting or rejecting a combination it
    /// should not is caught here even if every theory above degenerated to a no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// n3 + M1 (peer review, fix round 2). The PERMITTED count was 16 (<c>tls</c> on all 13
    /// dependency kinds, <c>mtls</c> on kafka only, both profiles on a service) and is now 4 —
    /// measured, by running this method against the tightened schema, not derived on paper. The
    /// 26/28 figures above are the enumerated SPACE this method walks and are unaffected by that
    /// tightening. The permitted count is no longer baked into the method NAME: the old name
    /// (<c>…IsExactlySixteen</c>) forced a RENAME on top of an edit every time the permitted set
    /// moved, and a stale name that disagrees with its own assertion is worse than no name at
    /// all. The assertion also moved from a bare COUNT to the explicit SET of permitted pairs —
    /// a count of 4 would be equally satisfied by four wrong pairs, and this is the enumeration
    /// REQ-022's acceptance criterion actually names.
    /// </para>
    /// <para>
    /// MINOR-2 (peer review, fix round 3): this block documents the METHOD below and had been
    /// orphaned onto <see cref="s_expectedPermittedCombinations"/> by round two's edit, which
    /// inserted that field between the documentation and the method it describes — leaving the
    /// field with two <c>&lt;summary&gt;</c> elements and the coverage-floor rationale attached
    /// to a string array. Moved back; the field keeps its own one-line summary above.
    /// </para>
    /// </remarks>
    [Fact]
    public void PermittedCombinations_AcrossAllDependencyKindsAndServiceProfiles_AreExactlyTheWiredSet()
    {
        var registry = StepRegistry();
        var permitted = new List<string>();

        foreach (var kind in s_dependencyKinds)
        {
            foreach (var profile in new[] { "tls", "mtls" })
            {
                if (DocumentValidator.Validate(BuildDependencyYaml(kind, profile), registry).IsValid)
                {
                    permitted.Add($"dependency:{kind}/{profile}");
                }
            }
        }

        foreach (var profile in new[] { "tls", "mtls" })
        {
            if (DocumentValidator.Validate(BuildServiceYaml(profile), registry).IsValid)
            {
                permitted.Add($"service/{profile}");
            }
        }

        Assert.Equal(
            s_expectedPermittedCombinations,
            permitted.OrderBy(p => p, System.StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// n2 (peer review, fix round 2): <see cref="SecurityProfileRegistry.ServiceTargetKind"/> is a
    /// stringly-typed sentinel sharing one value space with the dependency <c>type</c> enum. A
    /// fourteenth dependency kind literally named <c>service</c> would alias it silently — every
    /// service-scoped wiring would begin claiming that dependency kind — and
    /// <see cref="DependencyKindsEnumerated_MatchesSchemasOwnTypeEnum"/> below would NOT catch it:
    /// that guard compares SETS of kind names between the schema and this file's own array, and
    /// a name present in both is exactly what it is designed to accept. Asserted against the LIVE
    /// composed schema, not the local array, so the collision is caught the moment the schema
    /// gains such a kind, whether or not this file was updated with it.
    /// </summary>
    [Fact]
    public void ServiceTargetKind_DoesNotCollideWithAnyDependencyKind()
    {
        var composedSchemaJson = SchemaComposer.ComposeSchemaJson(StepRegistry());
        using var schemaDocument = JsonDocument.Parse(composedSchemaJson);

        var schemaKinds = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("dependency")
            .GetProperty("properties")
            .GetProperty("type")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        Assert.DoesNotContain(SecurityProfileRegistry.ServiceTargetKind, schemaKinds);
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

    // ── M2: none of this seam is published ───────────────────────────────────────────

    /// <summary>
    /// M2 (peer review, fix round 2): <c>Vouchfx.Engine.Compilation</c> is a PACKABLE project with
    /// <c>GenerateDocumentationFile=true</c>, so any <c>public</c> type in it ships on NuGet with
    /// its XML docs attached. A previous round shipped this entire security-profile seam that way
    /// while its own XML summary told the reader the interface "is not published for out-of-tree
    /// implementation yet" — prose a consumer would read out of the package that published it.
    /// The spec's Out-of-scope section is explicit that no extension interface ships until a
    /// second profile exists to design a frozen abstraction from; this test makes that a
    /// compile-visible fact rather than a comment, so re-widening any of these types is a
    /// deliberate, test-failing publication decision instead of a visibility tidy-up nobody
    /// reviews.
    /// </summary>
    [Fact]
    public void EverySecurityProfileSeamType_IsInternal_AndThereforeUnpublished()
    {
        var seamTypes = new[]
        {
            typeof(SecurityProfileRegistry),
            typeof(ISecurityProfileWiring),
            typeof(RegisteredSecurityProfileWiring),
            typeof(DuplicateSecurityProfileException),
            typeof(SecurityProfileWiringAttribute),
        };

        // Control: IsVisible really does report `true` for something published from this same
        // assembly, so a green assertion below means "internal", never "the predicate is inert".
        Assert.True(typeof(DocumentValidator).IsVisible);

        var published = seamTypes.Where(t => t.IsVisible).Select(t => t.FullName).ToList();

        Assert.True(published.Count == 0,
            "These security-profile seam types are PUBLIC and therefore ship in the " +
            "Vouchfx.Engine.Compilation NuGet package: " + string.Join(", ", published) +
            ". Publishing them freezes a shape one implementation is not enough to design. If " +
            "that is genuinely intended, it is an SDK decision — record it in the spec and the " +
            "CHANGELOG, not by relaxing this test.");

        // The engine assembly really is packable — otherwise the assertion above guards nothing.
        // Read from the same csproj the gate is about, so a change to IsPackable is visible here.
        var csproj = Path.Combine(
            RepositoryRoot(), "src", "Engine", "Vouchfx.Engine.Compilation",
            "Vouchfx.Engine.Compilation.csproj");
        Assert.Contains("<IsPackable>true</IsPackable>", File.ReadAllText(csproj), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test binary to the repository root (the directory holding
    /// <c>vouchfx.sln</c>) — the same discovery shape other file-reading gates in this suite use.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── m3: the multi-assembly BuildAndFreeze overload actually discovers ─────────────

    /// <summary>
    /// m3 (peer review, fix round 2): <see cref="SecurityProfileRegistry.BuildAndFreeze(IEnumerable{Assembly})"/>
    /// scans for <c>[SecurityProfileWiring]</c>-decorated types, and the critic's finding was
    /// that the attribute's own visibility made that parameter unsatisfiable from anywhere but
    /// the engine assembly — a surface that could not do what its summary described. Making the
    /// whole registry seam <c>internal</c> (M2) fixed it from the other direction: this test
    /// ASSEMBLY is granted <c>InternalsVisibleTo</c>, so it can declare a decorated wiring of its
    /// own, and the scan finds it across the assembly boundary. Nothing out-of-tree can, by
    /// design — that is the publication decision, not a defect.
    /// </summary>
    [Fact]
    public void BuildAndFreeze_ScansAnotherAssembly_AndDiscoversItsDecoratedWiring()
    {
        var registry = SecurityProfileRegistry.BuildAndFreeze(
            new[] { typeof(SecurityProfileRegistryTests).Assembly });

        Assert.True(registry.TryGet(TestOnlyProfileName, out var wiring),
            "Expected the scan of THIS test assembly to discover its own decorated wiring type. " +
            "If this fails, [SecurityProfileWiring] is no longer applicable from a grantee " +
            "assembly and BuildAndFreeze(IEnumerable<Assembly>)'s own summary is false again.");
        Assert.Equal(TestOnlyProfileName, wiring!.Profile);

        // The scan is genuinely per-assembly: this registry contains ONLY this assembly's
        // wiring, never the engine's built-ins, so a false positive from BuiltIn leaking in is
        // ruled out.
        Assert.False(registry.TryGet("tls", out _));
    }

    // ── n1: the unknown-profile message is bounded ───────────────────────────────────

    /// <summary>
    /// n1 (peer review, fix round 2): the "unknown security profile" message truncates an
    /// author-controlled profile value at the SAME 200-character bound
    /// <c>SchemaErrorCollector.TruncateForDisplay</c> applies, with the same
    /// "… (N chars total)" tail. Previously the bound existed only on the
    /// <c>FormatConstError</c> path, so the two renderings of the identical value differed above
    /// 200 characters — and M1 deleted that path, which would have removed the bound entirely
    /// rather than unified it. <c>profile</c> is an open string pattern with no length limit of
    /// its own and this text is serialised verbatim into <c>vouchfx validate --json</c>'s
    /// golden-pinned <c>ValidateJsonDiagnostic(Stage, Message)</c> document, so the bound is a
    /// security property, not cosmetics. (MINOR-1, fix round 4: the surface named here used to be
    /// the §14 JSON Lines event stream, which this text never reaches — no event record carries a
    /// schema-validation message; the bound's justification is unaffected, since
    /// <c>validate --json</c> is <c>System.Text.Json</c>-serialised just the same.)
    /// </summary>
    [Fact]
    public void UnknownProfileMessage_TruncatesAtTheSameBoundAsSchemaErrorCollector()
    {
        const int bound = SecurityProfileRegistry.MaxOffendingValueChars;
        var longProfile = new string('a', 250);

        var message = SecurityProfileRegistry.BuiltIn.DescribeUnknownProfile(longProfile);

        Assert.Contains($"{new string('a', bound)}… (250 chars total)", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain(longProfile, message, System.StringComparison.Ordinal);

        // A value AT the bound is rendered verbatim — the truncation is exclusive, matching
        // TruncateForDisplay's own '<=' early-out.
        var atBound = new string('b', bound);
        Assert.Contains($"'{atBound}'", SecurityProfileRegistry.BuiltIn.DescribeUnknownProfile(atBound),
            System.StringComparison.Ordinal);
    }

    /// <summary>
    /// SEC-2 (peer review, fix round 3): the bound is a CHAR index, so a value whose astral-plane
    /// character straddles it must back off rather than cut between the high surrogate and its
    /// low-surrogate partner. Measured before the fix: <c>ESC + 198×'a' + U+1F600</c> (length 201)
    /// landed the cut exactly between the two halves and <c>System.Text.Json</c> wrote U+FFFD.
    /// </summary>
    /// <remarks>
    /// Asserted two ways, because "does not end with a high surrogate" alone would pass for a
    /// string that is malformed further in: the truncated segment must contain no unpaired
    /// surrogate at all, AND the whole message must survive a <c>System.Text.Json</c> round trip
    /// byte-for-byte — that serialisation is the surface (<c>vouchfx validate --json</c>) this
    /// text actually reaches, and a lone surrogate is where it would silently substitute U+FFFD.
    /// </remarks>
    [Fact]
    public void UnknownProfileMessage_WhereTheBoundWouldSplitASurrogatePair_BacksOffToKeepThePairIntact()
    {
        const int bound = SecurityProfileRegistry.MaxOffendingValueChars;
        const string astral = "😀"; // U+1F600 — one high + one low surrogate.

        // The high surrogate sits at index bound-1 (the last INCLUDED char of a naive slice) and
        // its low half at index bound (the first EXCLUDED one) — the exact straddle.
        var profile = new string('a', bound - 1) + astral + "trailing-content-past-the-pair";
        Assert.True(char.IsHighSurrogate(profile[bound - 1]) && char.IsLowSurrogate(profile[bound]),
            "Fixture precondition: the surrogate pair must straddle the truncation bound.");

        var message = SecurityProfileRegistry.BuiltIn.DescribeUnknownProfile(profile);

        // The pair is dropped whole: the rendered value is the 'a' run, never a lone surrogate.
        Assert.Contains($"'{new string('a', bound - 1)}… ({profile.Length} chars total)'", message,
            System.StringComparison.Ordinal);

        for (var i = 0; i < message.Length; i++)
        {
            if (char.IsHighSurrogate(message[i]))
            {
                Assert.True(i + 1 < message.Length && char.IsLowSurrogate(message[i + 1]),
                    $"Unpaired high surrogate at index {i} — the truncated message is invalid UTF-16.");
                i++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(message[i]),
                $"Unpaired low surrogate at index {i} — the truncated message is invalid UTF-16.");
        }

        // The serialisation surface this text reaches ('vouchfx validate --json') must round-trip
        // it unchanged; a lone surrogate is where System.Text.Json substitutes U+FFFD instead.
        Assert.Equal(message, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(message)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private const string TestOnlyProfileName = "acme-sasl";

    /// <summary>
    /// A wiring declared in the TEST assembly, existing solely to prove
    /// <see cref="BuildAndFreeze_ScansAnotherAssembly_AndDiscoversItsDecoratedWiring"/>'s
    /// cross-assembly discovery. It is never registered in
    /// <see cref="SecurityProfileRegistry.BuiltIn"/> (a different assembly is scanned there), so
    /// its presence cannot leak into any production-registry assertion in this repository.
    /// </summary>
    [SecurityProfileWiring]
    internal sealed class TestOnlyProfileWiring : ISecurityProfileWiring
    {
        public string Profile => TestOnlyProfileName;

        public bool AppliesTo(string targetKind) => false;
    }

    private sealed class StubWiring : ISecurityProfileWiring
    {
        public StubWiring(string profile) => Profile = profile;

        public string Profile { get; }

        public bool AppliesTo(string targetKind) => true;
    }
}
