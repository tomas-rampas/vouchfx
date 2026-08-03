// Vouchfx.Engine.Orchestration.Tests — sidecar-write-site drift guard (G-E, gatekeeper, fix
// round 3).
//
// EnvironmentMapper.GetDependencyServiceSidecarNames is the declarative "which dependency
// types stage an EXTRA suffixed svc::<name>-<suffix> sidecar key, beyond the dependency's own
// bare name" table that ProviderPipeline's collision guard AND its DeclaredServices derivation
// (M1/m7, fix round 3) both consult. Its default arm (`_ => Array.Empty<string>()`) is
// exhaustive today but UNENFORCED: nothing stops a future dependency registration from writing
// a THIRD suffixed `serviceEndpoints[...]` key inside its own Build lambda without adding a
// matching arm to that switch — the guard and DeclaredServices would then silently never learn
// the new name exists, exactly the class of gap this branch's own M1/m7 fixes just closed for
// the two shapes that exist today (mailpit's SMTP sidecar, Kafka's schema-registry sidecar).
//
// This test reads EnvironmentMapper.cs's OWN source text — rather than a hand-maintained list
// this file would also have to remember to update — and fails if a suffixed
// 'serviceEndpoints[...] = ' write site appears that GetDependencyServiceSidecarNames's own
// switch body does not account for (by helper-function call, or a local-variable alias of
// one). Adding a new arm to that switch automatically extends what this guard accepts; adding
// a suffixed write site WITHOUT a matching arm fails this test.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class EnvironmentMapperSidecarDriftGuardTests
{
    // Mirrors ExamplesCompileTests.ResolveRepoRoot / Sprint11ReferenceCompileTests.
    // ResolveRepoRoot: walk up from the test assembly's output directory
    // (bin/Debug|Release/net8.0 under tests/Vouchfx.Engine.Orchestration.Tests/) to the repo
    // root.
    private static string ResolveRepoRoot()
    {
        var assemblyDir =
            Path.GetDirectoryName(typeof(EnvironmentMapperSidecarDriftGuardTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }

    private static string EnvironmentMapperSourcePath => Path.Combine(
        ResolveRepoRoot(), "src", "Engine", "Vouchfx.Engine.Orchestration", "EnvironmentMapper.cs");

    [Fact]
    public void ServiceEndpointsWriteSites_EverySuffixedKey_IsAccountedForByGetDependencyServiceSidecarNames()
    {
        var source = File.ReadAllText(EnvironmentMapperSourcePath);

        // 1. Extract GetDependencyServiceSidecarNames's own switch body, and the set of
        //    '*ServiceName(' helper functions it calls — the declared source of truth.
        var bodyMatch = Regex.Match(
            source,
            @"GetDependencyServiceSidecarNames\([^)]*\)\s*=>\s*spec\.Type\s*switch\s*\{(?<body>[\s\S]*?)\};",
            RegexOptions.Singleline);
        Assert.True(
            bodyMatch.Success,
            "Could not locate GetDependencyServiceSidecarNames's switch body — its shape " +
            "changed enough that this guard needs updating alongside it.");

        var switchBody = bodyMatch.Groups["body"].Value;
        var helperNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match hm in Regex.Matches(switchBody, @"\b(\w+ServiceName)\s*\("))
        {
            helperNames.Add(hm.Groups[1].Value);
        }

        Assert.NotEmpty(helperNames); // Today: MailpitSmtpServiceName, KafkaSchemaRegistryServiceName.

        // 2. For each declared helper, accept both its direct call form ('Helper(name)') and
        //    any local-variable alias a Build lambda assigns from it ('var x = Helper(name);') —
        //    keyed per-helper, so step 4 below can confirm EACH helper is actually used as a
        //    write site via at least one of its accepted forms, not merely that the accepted
        //    set as a whole is fully covered (which a single well-used helper could satisfy
        //    while masking a second, dead one).
        var formsByHelper = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var acceptedMarkers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var helper in helperNames)
        {
            var forms = new List<string> { $"{helper}(name)" };
            foreach (Match am in Regex.Matches(source, $@"var\s+(\w+)\s*=\s*{Regex.Escape(helper)}\(name\)\s*;"))
            {
                forms.Add(am.Groups[1].Value);
            }

            formsByHelper[helper] = forms;
            acceptedMarkers.UnionWith(forms);
        }

        // 3. Every 'serviceEndpoints[<expr>] = ' ASSIGNMENT (never '==', a read, or a comment
        //    mentioning the pattern) whose <expr> is not the plain dependency name must use one
        //    of the accepted markers above.
        var writeSites = Regex.Matches(source, @"serviceEndpoints\[([^\]]+)\]\s*=(?!=)");
        Assert.True(
            writeSites.Count >= 3,
            "Expected at least the bare-name writes plus the two known suffixed writes — the " +
            "regex itself may have stopped matching if EnvironmentMapper.cs's formatting changed.");

        var suffixedSites = new List<string>();
        foreach (Match wm in writeSites)
        {
            var expr = wm.Groups[1].Value.Trim();
            if (!string.Equals(expr, "name", StringComparison.Ordinal))
            {
                suffixedSites.Add(expr);
            }
        }

        Assert.All(suffixedSites, expr => Assert.Contains(expr, acceptedMarkers));

        // 4. And the reverse, per helper: each declared helper must actually be USED as a
        //    write site, via at least one of ITS OWN accepted forms — if one stops appearing
        //    (the sidecar shape was removed/renamed in the Build lambda but not in the switch),
        //    GetDependencyServiceSidecarNames now advertises a dead arm, which this guard
        //    should also catch.
        foreach (var (helper, forms) in formsByHelper)
        {
            Assert.True(
                forms.Exists(form => suffixedSites.Contains(form)),
                $"Helper '{helper}' is declared in GetDependencyServiceSidecarNames's switch " +
                "but is never used at a serviceEndpoints[...] write site (directly, or via a " +
                "local-variable alias) — either the switch carries a dead arm, or this guard's " +
                "alias-detection regex needs updating to recognise the current shape.");
        }
    }
}
