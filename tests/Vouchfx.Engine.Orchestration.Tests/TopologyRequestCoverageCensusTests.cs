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
    /// THE ELEMENT FRAMING, isolated: two sets of the SAME cardinality whose elements CONCATENATE to
    /// the same characters — <c>{"ab", "c"}</c> and <c>{"a", "bc"}</c> — must produce different
    /// digest inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The cardinalities are equal on purpose, and the first draft of this arm got that
    /// wrong.</strong> It used <c>{"ab"}</c> against <c>{"a", "b"}</c>; the drill then deleted the
    /// length framing and the arm stayed GREEN, because the two sets have different counts and the
    /// count prefix separated them on its own. An arm that a sibling mechanism can carry measures
    /// the sibling. Same count, same concatenation, different boundaries is the smallest input the
    /// framing alone decides.
    /// </para>
    /// <para>
    /// <strong>What this does and does not say about separators.</strong> No single pair can fail
    /// "every separator scheme" — for any fixed pair, some separator character distinguishes it, and
    /// for any fixed separator, some author-writable pair collides. That asymmetry is the whole
    /// argument for framing, and it is why <c>TopologyFingerprintTests</c>' document-level collision
    /// arm is comma-specific: it shows the reachable YAML for the separator this code actually used.
    /// What these two census arms pin is the PROPERTY that replaces the search for a safe separator
    /// — the encoding records where each element ends, and how many elements each set holds.
    /// </para>
    /// </remarks>
    [Fact]
    public void TargetsDifferingOnlyInWhereTheElementEnds_ProduceDifferentDigestInputs()
    {
        var left = RequestWith(kafka: Set(), endpointConsuming: Set("ab", "c"));
        var right = RequestWith(kafka: Set(), endpointConsuming: Set("a", "bc"));

        // The premise, asserted rather than assumed: equal cardinality and equal concatenation, so
        // neither the count prefix nor the raw characters can be what separates them below.
        Assert.Equal(
            left.EndpointConsumingTargets.Count, right.EndpointConsumingTargets.Count);
        Assert.Equal(
            string.Concat(left.EndpointConsumingTargets.OrderBy(t => t, StringComparer.Ordinal)),
            string.Concat(right.EndpointConsumingTargets.OrderBy(t => t, StringComparer.Ordinal)));

        Assert.NotEqual(
            left.ComputeFingerprintInput("ENVHASH"), right.ComputeFingerprintInput("ENVHASH"));
    }

    /// <summary>
    /// THE COUNT PREFIX, pinned: the same element in the Kafka set and in the endpoint-consuming set
    /// must produce different digest inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two sets are adjacent in the digest input, so without a per-set element count their
    /// frames form one undifferentiated run and MOVING an element from one set to the other leaves
    /// the input byte-identical. That is not a cosmetic difference: the Kafka set decides the STAGED
    /// FORM (a bare <c>host:port</c> authority rather than a URL) and the confirmation level, so the
    /// two arrangements describe materially different topologies.
    /// </para>
    /// <para>
    /// <strong>This property had no test until the peer review derived it</strong>, and no drill in
    /// the earlier round covered it: deleting the count prefix left every fingerprint arm green,
    /// because each of those arms moves an element INTO or OUT OF the union rather than BETWEEN the
    /// two sets.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSameTargetInEitherSet_ProducesDifferentDigestInputs()
    {
        var kafkaSpeaking = RequestWith(kafka: Set("a"), endpointConsuming: Set());
        var endpointOnly = RequestWith(kafka: Set(), endpointConsuming: Set("a"));

        Assert.NotEqual(
            kafkaSpeaking.ComputeFingerprintInput("ENVHASH"),
            endpointOnly.ComputeFingerprintInput("ENVHASH"));
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
        var forward = RequestWith(Set("alpha", "beta"), Set("alpha", "beta"));
        var reversed = RequestWith(Set("beta", "alpha"), Set("beta", "alpha"));

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A request whose only interesting members are its two target sets — every other input held
    /// fixed, so a difference between two of these is attributable to the sets alone.
    /// </summary>
    /// <remarks>
    /// Uses the PUBLIC constructor rather than a factory deliberately: these arms are about the
    /// ENCODING, so they must be able to state target sets a single AST would never produce (an
    /// element in the Kafka set and not in its superset, for instance).
    /// </remarks>
    private static TopologyRequest RequestWith(
        IReadOnlySet<string> kafka, IReadOnlySet<string> endpointConsuming) =>
        new(
            Environment: null,
            AppHostAssemblyName: "host",
            StartupTimeout: TopologyRequest.DefaultStartupTimeout,
            SeedBaseDirectory: "/dir",
            KafkaSpeakingTargets: kafka,
            EndpointConsumingTargets: endpointConsuming);

    private static HashSet<string> Set(params string[] targets) =>
        new(targets, StringComparer.Ordinal);
}
