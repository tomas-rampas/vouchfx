// Vouchfx.Engine.Orchestration.Tests — TopologyRequest completeness censuses (#364). No Docker.
//
// WHY A CENSUS AND NOT A UNIT TEST. #364's first two defects are the same defect: an optional
// argument dropped from one of three hand-maintained argument lists for SuiteTopology.StartAsync.
// Collapsing those into TopologyRequest removes the three lists — but a SIXTH optional parameter
// added to StartAsync and not to the record would be silently defaulted at the one remaining call
// site, which is the identical degrade-quietly shape one level up. Nothing about the fix is
// self-enforcing without a gate.
//
// REFLECTION, not a source regex, because reflection can see the property this class must have and
// a regex can only see the text someone remembered to write. The repo's existing censuses use
// source scanning where the property is textual (a call site, a helper call); here the property is
// a parameter set, and the compiler already knows it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class TopologyRequestCoverageCensusTests
{
    /// <summary>
    /// The parameters of <see cref="SuiteTopology.StartAsync"/> that are DOCUMENT-DERIVED — i.e.
    /// everything except the cancellation token and the security accessor, whose exclusion is a
    /// decision recorded in <see cref="TopologyRequest"/>'s own header (the accessor owns
    /// certificate lifetimes and is not a document input, so it must not enter the fingerprint).
    /// </summary>
    private static readonly HashSet<string> Excluded =
        new(StringComparer.Ordinal) { "cancellationToken", "securityConfiguration" };

    /// <summary>
    /// EVERY document-derived parameter of <see cref="SuiteTopology.StartAsync"/> has a matching
    /// member on <see cref="TopologyRequest"/>, and vice versa.
    /// </summary>
    /// <remarks>
    /// SET equality in BOTH directions. A missing member is the #364 defect returning one level up;
    /// an extra member is a value the request carries into the fingerprint that the topology was
    /// never built from, which would rebuild <c>--watch</c>'s topology for a reason nothing can act
    /// on.
    /// </remarks>
    [Fact]
    public void TopologyRequest_CoversEveryDocumentDerivedStartAsyncParameter()
    {
        var startParameters = typeof(SuiteTopology)
            .GetMethod(nameof(SuiteTopology.StartAsync), BindingFlags.Public | BindingFlags.Static)!
            .GetParameters()
            .Select(p => p.Name!)
            .Where(name => !Excluded.Contains(name))
            .ToHashSet(StringComparer.Ordinal);

        // A census matching nothing passes for free, so the scan is proved non-empty before
        // anything is concluded from it — the shape that would follow a rename of StartAsync.
        Assert.True(
            startParameters.Count > 0,
            "No document-derived parameters were found on SuiteTopology.StartAsync. Either it was "
            + "renamed or every parameter is now excluded; either way this census is asserting "
            + "nothing and must be re-pointed rather than left green.");

        var members = typeof(TopologyRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(startParameters, members);
    }

    /// <summary>
    /// The member COUNT is pinned, so adding one forces a decision about the fingerprint.
    /// </summary>
    /// <remarks>
    /// The census above would go green on a new member/parameter PAIR, which is the common way a
    /// value reaches the topology. That is the moment someone must decide whether the new input
    /// belongs in <c>ScenarioRunner.ComputeTopologyFingerprint</c> — because an input the topology
    /// was built from and the fingerprint ignores is exactly #370's recorded residual, in a new
    /// place. The failure message is the whole point of the assertion.
    /// </remarks>
    [Fact]
    public void TopologyRequest_HasExactlySixMembers()
    {
        var members = typeof(TopologyRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            members.Count == 6,
            $"TopologyRequest now has {members.Count} members ({string.Join(", ", members)}) rather "
            + "than 6. If you ADDED one: decide whether it is an input the built topology depends "
            + "on, and if it is, add it to ScenarioRunner.ComputeTopologyFingerprint — an input the "
            + "topology was built from that the fingerprint ignores is #370's recorded residual in "
            + "a new place, and --watch will reuse a topology it should have rebuilt. Then update "
            + "this count.");
    }

    /// <summary>
    /// Two requests carrying the SAME multi-element target sets produce the same digest input, and
    /// the elements are ordered ORDINALLY rather than in enumeration order — the property the
    /// fingerprint's sort exists to guarantee.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IReadOnlySet{T}"/> carries no ordering contract, so a digest that took either set
    /// in enumeration order could differ between two equal requests. That would not fail loudly — it
    /// would rebuild the topology on every save, quietly turning <c>--watch</c> into <c>run</c> in a
    /// loop.
    /// </para>
    /// <para>
    /// <strong>The sets carry TWO elements each, inserted in OPPOSITE orders, and that is the whole
    /// design of this arm.</strong> An earlier form built both requests from one empty-step AST: the
    /// sets were empty, so an unsorted join would have produced an identical (empty) result and the
    /// test would have passed against the very defect it names. Two elements inserted in opposite
    /// orders is the smallest fixture a missing sort actually fails, and the ordinal ordering is
    /// asserted directly rather than inferred from the two digests agreeing.
    /// </para>
    /// </remarks>
    [Fact]
    public void ForScenario_IsDeterministic_AndOrdersTargetsOrdinally()
    {
        var forward = new TopologyRequest(
            Environment: null,
            AppHostAssemblyName: "host",
            StartupTimeout: TopologyRequest.DefaultStartupTimeout,
            SeedBaseDirectory: "/dir",
            KafkaSpeakingTargets: new HashSet<string>(StringComparer.Ordinal) { "alpha", "beta" },
            EndpointConsumingTargets: new HashSet<string>(StringComparer.Ordinal) { "alpha", "beta" });

        var reversed = new TopologyRequest(
            Environment: null,
            AppHostAssemblyName: "host",
            StartupTimeout: TopologyRequest.DefaultStartupTimeout,
            SeedBaseDirectory: "/dir",
            KafkaSpeakingTargets: new HashSet<string>(StringComparer.Ordinal) { "beta", "alpha" },
            EndpointConsumingTargets: new HashSet<string>(StringComparer.Ordinal) { "beta", "alpha" });

        var forwardInput = forward.ComputeFingerprintInput("ENVHASH");

        Assert.Equal(forwardInput, reversed.ComputeFingerprintInput("ENVHASH"));

        // Ordinal, and asserted on the digest input itself: 'alpha' must precede 'beta' whichever
        // order the set enumerates in.
        Assert.True(
            forwardInput.IndexOf("alpha", StringComparison.Ordinal)
                < forwardInput.IndexOf("beta", StringComparison.Ordinal),
            "target sets must be ordinally sorted into the digest input: " + forwardInput);

        // And the ForScenario factory itself is stable over one AST, which is what production calls.
        var ast = new ScenarioAst(
            Metadata: null,
            Environment: null,
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

        Assert.Equal(
            TopologyRequest.ForScenario(ast, "host", "/dir").ComputeFingerprintInput("ENVHASH"),
            TopologyRequest.ForScenario(ast, "host", "/dir").ComputeFingerprintInput("ENVHASH"));
    }
}
