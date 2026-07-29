// Vouchfx.Engine.Planning — StepFamilyRoles (REQ-004 bullet 3: asserting vs non-asserting).
//
// REQ-004's third coverage-gap kind ("an environment dependency that IS targeted by at
// least one declared step but by no step of an ASSERTING family") needs a classification of
// which of the eleven shipped step families observe/assert a seam versus merely drive or
// operate opaquely against it. Kept as its own file (not inside CoverageGapAnalyser.cs or
// DependencyKindStepMap.cs) so the T2 (coverage gaps) and T3 (vocabulary gaps) analysers can
// both read it without either one owning — or needing to edit — the other's file.

namespace Vouchfx.Engine.Planning;

/// <summary>
/// Classifies each of the eleven shipped step families as asserting/observing (the step
/// verifies something about the target) or non-asserting (the step only drives the target,
/// or is opaque to static classification).
/// </summary>
/// <remarks>
/// <para>
/// A dependency that is targeted only by non-asserting steps (e.g. published to via
/// <c>mq-publish.kafka</c> but never consumed via an asserting family) is an exercised-but-
/// unverified seam — REQ-004's <c>dependency-not-asserted</c> finding.
/// </para>
/// <para>
/// A test asserting every family currently registered in a live <see cref="Vouchfx.Sdk.StepKindRegistry"/>
/// is classified one way or the other (never silently defaulted) belongs beside the
/// registry the test builds — see <c>DependencyKindStepMapDriftTests</c>'s recommended third
/// drift test — so that a new family forces a classification decision in the same change.
/// </para>
/// </remarks>
public static class StepFamilyRoles
{
    /// <summary>
    /// Step families whose steps verify or observe something about their target:
    /// <c>db-assert</c>, <c>cache-assert</c>, <c>mq-expect</c>, <c>mail-expect</c>,
    /// <c>metrics-assert</c>, <c>storage-assert</c>, <c>trace-expect</c>,
    /// <c>webhook-listen</c>, and <c>http</c> (an HTTP call asserts on the response).
    /// </summary>
    public static IReadOnlySet<string> AssertingFamilies { get; } = new HashSet<string>(
        StringComparer.Ordinal)
    {
        "db-assert",
        "cache-assert",
        "mq-expect",
        "mail-expect",
        "metrics-assert",
        "storage-assert",
        "trace-expect",
        "webhook-listen",
        "http",
    };

    /// <summary>
    /// Step families that drive a target or are opaque to static classification:
    /// <c>mq-publish</c> (only ever publishes; never asserts) and <c>script</c> (arbitrary
    /// C# — its behaviour cannot be classified without executing it).
    /// </summary>
    public static IReadOnlySet<string> NonAssertingFamilies { get; } = new HashSet<string>(
        StringComparer.Ordinal)
    {
        "mq-publish",
        "script",
    };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="family"/> is a known asserting
    /// family (case-sensitive, matching the DSL's lowercase family tokens).
    /// </summary>
    /// <param name="family">The step family token, e.g. <c>db-assert</c>.</param>
    public static bool IsAsserting(string family) =>
        !string.IsNullOrEmpty(family) && AssertingFamilies.Contains(family);
}
