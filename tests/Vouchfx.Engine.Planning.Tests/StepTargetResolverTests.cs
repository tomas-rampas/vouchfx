// Vouchfx.Engine.Planning.Tests — StepTargetResolverTests (M3 Planner, REQ-004 "Target
// resolution", flags F-16 and F-17).
//
// Closes a spec-conformance gap found by a full-spec review: Ingest/StepTargetResolver.cs
// reads correctly on inspection, but neither of REQ-004's two target-resolution exclusions
// had a fixture or a direct unit test anywhere in this project or Vouchfx.Cli.Tests. Untested
// MUST behaviour is not delivered — a later refactor could silently break either rule with no
// test to catch it. This file pins both:
//
//   - F-16 (Rule 1): a `target` containing a {placeholder} or ${secret:...} reference is
//     structurally unresolvable and MUST be treated as targeting nothing — never counted as
//     coverage of a dependency or service, never a crash.
//   - F-17 (Rule 2): `listener` (webhook-listen.http) / `receiver` (trace-expect.otlp) are
//     host-owned resource names, never target references — even when their value
//     coincidentally matches a declared dependency or service name.
//
// Rule 1 is pinned two ways: a direct call to the internal ResolveTarget/
// IsStructurallyUnresolvable methods (InternalsVisibleTo — the same pattern
// SuiteSetLoaderTests.cs and RunCorrelatorTests.cs already use for edge cases a fixture alone
// cannot cheaply or unambiguously pin), AND an end-to-end PlannerTestFixtures.Plan assertion
// against the resulting findings, proving the unresolvable-target steps are genuinely excluded
// from coverage and the analysis never throws.
//
// Rule 2 is pinned with the STRONG coincidental-match construction the review named: a
// webhook-listen.http/trace-expect.otlp step whose listener/receiver value is spelled EXACTLY
// like a declared dependency that is ALSO genuinely (and only) targeted by a real,
// non-asserting step (mq-publish.kafka). If the resolver ever fell back to reading
// listener/receiver as a target, that host-owned step would count as a second, ASSERTING
// "targeting" step for the dependency (both families are in StepFamilyRoles.AssertingFamilies),
// and CoverageGapAnalyser would silently treat the dependency as verified — exactly the
// false-negative the review warned about. These assertions fail immediately under that
// regression; see each test's comment for the exact mechanics.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class StepTargetResolverTests
{
    // ── F-16 (Rule 1): direct pin on IsStructurallyUnresolvable itself ───────────────────

    [Theory]
    [InlineData("{someVar}")]
    [InlineData("${secret:a/b}")]
    [InlineData("prefix-{someVar}-suffix")]
    public void IsStructurallyUnresolvable_PlaceholderOrSecretReference_ReturnsTrue(string value)
    {
        Assert.True(StepTargetResolver.IsStructurallyUnresolvable(value));
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("orders-db")]
    [InlineData("")]
    public void IsStructurallyUnresolvable_PlainNameOrEmpty_ReturnsFalse(string value)
    {
        Assert.False(StepTargetResolver.IsStructurallyUnresolvable(value));
    }

    // ── F-16 (Rule 1): direct pin on ResolveTarget over real, parsed StepNodes ───────────
    // A fixture/pipeline assertion alone cannot distinguish "returns null because the guard
    // filtered it" from "returns null because the literal placeholder string never matches a
    // declared name anyway" (removing the guard would return the raw "{apiName}"/
    // "${secret:...}" string, which still would not equal a declared name, so downstream
    // classification would still fail either way). Calling ResolveTarget directly is the only
    // way to pin the guard itself — mirrors SuiteSetLoaderTests.cs's own stated reason for
    // testing an Ingest internal directly rather than only through the public report.

    [Fact]
    public void ResolveTarget_PlaceholderAndSecretTargets_BothResolveToNull()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var loadResult = SuiteSetLoader.Load(
            PlannerTestFixtures.FixtureRoot("coverage/unresolvable-targets"), registry);
        var suite = Assert.Single(loadResult.Suites);

        var placeholderStep = suite.Ast.Steps.Single(s => s.Id == "call-api-with-placeholder-target");
        var secretStep = suite.Ast.Steps.Single(s => s.Id == "assert-cache-with-secret-target");

        Assert.Null(StepTargetResolver.ResolveTarget(placeholderStep));
        Assert.Null(StepTargetResolver.ResolveTarget(secretStep));
    }

    // ── F-16 (Rule 1) acceptance: end-to-end, against the resulting findings/inventory ──

    [Fact]
    public void UnresolvableTargets_AreNotCountedAsCoverageAndProduceNoException()
    {
        // "call-api-with-placeholder-target" nominally addresses the "api" service via
        // `target: "{apiName}"`; "assert-cache-with-secret-target" nominally addresses the
        // "cache" dependency via `target: "${secret:vault/cache-name}"`. Both forms MUST be
        // treated as targeting nothing (b): the call below must not throw, and (a) neither
        // step may count as coverage of the service/dependency it nominally names.
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/unresolvable-targets"));

        // (b) No exception — PlannerTestFixtures.Plan would have thrown by now if the
        // placeholder/secret target crashed the analysis.

        // (a) "api" still reads as uncovered: the http.rest step exists but its target never
        // resolves to "api", so it cannot satisfy VocabularyGapAnalyser's http-coverage check.
        var serviceFinding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
        Assert.Equal("api", serviceFinding.Target);
        Assert.Equal(PlanTargetKinds.Service, serviceFinding.TargetKind);

        // (a) "cache" still reads as uncovered: the cache-assert.redis step exists but its
        // target never resolves to "cache" either.
        var dependencyFinding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);
        Assert.Equal("cache", dependencyFinding.Target);
        Assert.Equal(PlanTargetKinds.Dependency, dependencyFinding.TargetKind);

        // Precedence check: "cache" has ZERO steps whose target actually resolves to it, so
        // it must be exclusively VocabularyGapAnalyser's territory (REQ-004/REQ-005
        // precedence) — never also reported as dependency-not-asserted, which would mean the
        // unresolvable target was miscounted as an (unverified) targeting step.
        Assert.DoesNotContain(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyNotAsserted);

        // Both steps' own step-never-exercised findings must show a null Target/TargetKind —
        // the direct, field-level proof that ResolveTarget excluded them.
        var placeholderStepFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised
                && f.StepId == "call-api-with-placeholder-target");
        Assert.Null(placeholderStepFinding.Target);
        Assert.Null(placeholderStepFinding.TargetKind);

        var secretStepFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised
                && f.StepId == "assert-cache-with-secret-target");
        Assert.Null(secretStepFinding.Target);
        Assert.Null(secretStepFinding.TargetKind);

        // Exactly: suite-never-run, 2×step-never-exercised, service-missing-http-step,
        // dependency-missing-step-type — nothing else (no history-health findings are
        // reachable with no event history at all).
        Assert.Equal(5, report.Findings.Count);
    }

    // ── F-17 (Rule 2) acceptance: webhook-listen.http's `listener` is never a target ────

    [Fact]
    public void WebhookListenerValue_CoincidentallyMatchingDeclaredNames_IsNeverReadAsATarget()
    {
        // "queue" (kafka) is genuinely targeted ONLY by "publish-to-queue" (mq-publish.kafka,
        // a non-asserting family) — REQ-004's "some declared steps, none asserting" row, which
        // MUST yield a dependency-not-asserted finding. The fixture ALSO declares a
        // webhook-listen.http step whose `listener: queue` is spelled exactly like that same
        // dependency, and another whose `listener: web` matches the declared "web" service.
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/listener-name-collision"));

        // THE key assertion: if the resolver ever read `listener` as a target fallback, the
        // webhook-listen.http step (an ASSERTING family per StepFamilyRoles) would count as a
        // second targeting step for "queue", CoverageGapAnalyser's hasAssertingStep check
        // would flip true, and this finding would silently disappear — the dependency would
        // read as verified when it never actually was. This assertion fails immediately under
        // that regression.
        var dependencyNotAsserted = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyNotAsserted);
        Assert.Equal("queue", dependencyNotAsserted.Target);
        Assert.Equal(PlanTargetKinds.Dependency, dependencyNotAsserted.TargetKind);

        // The genuinely-targeting step's own target DOES resolve/classify normally — proving
        // the fixture exercises real resolution, not merely an empty/inert suite.
        var publishStepFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised && f.StepId == "publish-to-queue");
        Assert.Equal("queue", publishStepFinding.Target);
        Assert.Equal(PlanTargetKinds.Dependency, publishStepFinding.TargetKind);

        // Direct, field-level proof for BOTH webhook-listen.http steps: their own
        // step-never-exercised findings must show a null Target/TargetKind, even though one's
        // `listener` value matches a dependency name and the other's matches a service name.
        var queueListenerFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised
                && f.StepId == "listen-for-queue-callback");
        Assert.Null(queueListenerFinding.Target);
        Assert.Null(queueListenerFinding.TargetKind);

        var webListenerFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised
                && f.StepId == "listen-for-web-callback");
        Assert.Null(webListenerFinding.Target);
        Assert.Null(webListenerFinding.TargetKind);

        // "web" (service) has no http.* step targeting it — reported regardless of the F-17
        // bug (VocabularyGapAnalyser's service check filters by step family "http" first, and
        // webhook-listen.http never matches that filter), asserted here only to pin the
        // fixture's exact, complete finding set alongside the assertions above that DO detect
        // the regression.
        var serviceFinding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
        Assert.Equal("web", serviceFinding.Target);

        // "queue"'s sole REQ-005 candidate (mq-expect.kafka) is unused — REQ-004/REQ-005
        // precedence row 2's double report. Its own correctness is
        // VocabularyGapAnalyserTests's concern; asserted here only to pin the exact set.
        Assert.Single(report.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);

        // Exactly: suite-never-run, 3×step-never-exercised, dependency-not-asserted,
        // dependency-missing-step-type, service-missing-http-step — nothing else.
        Assert.Equal(7, report.Findings.Count);
    }

    // ── F-17 (Rule 2) acceptance: trace-expect.otlp's `receiver` is never a target ──────

    [Fact]
    public void OtlpReceiverValue_CoincidentallyMatchingDeclaredNames_IsNeverReadAsATarget()
    {
        // Mirrors WebhookListenerValue_CoincidentallyMatchingDeclaredNames_IsNeverReadAsATarget
        // exactly, for the OTHER host-owned field flag F-17 names: trace-expect.otlp's
        // `receiver`. "events" (kafka) is genuinely targeted ONLY by "publish-event"
        // (mq-publish.kafka, non-asserting); a trace-expect.otlp step's `receiver: events`
        // matches that dependency's name, and another's `receiver: api` matches the declared
        // "api" service.
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/receiver-name-collision"));

        // THE key assertion — see the webhook-listen.http test above for the exact mechanics
        // this guards against (trace-expect is likewise in StepFamilyRoles.AssertingFamilies).
        var dependencyNotAsserted = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyNotAsserted);
        Assert.Equal("events", dependencyNotAsserted.Target);
        Assert.Equal(PlanTargetKinds.Dependency, dependencyNotAsserted.TargetKind);

        var publishStepFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised && f.StepId == "publish-event");
        Assert.Equal("events", publishStepFinding.Target);
        Assert.Equal(PlanTargetKinds.Dependency, publishStepFinding.TargetKind);

        var eventsReceiverFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised && f.StepId == "watch-events-trace");
        Assert.Null(eventsReceiverFinding.Target);
        Assert.Null(eventsReceiverFinding.TargetKind);

        var apiReceiverFinding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised && f.StepId == "watch-api-trace");
        Assert.Null(apiReceiverFinding.Target);
        Assert.Null(apiReceiverFinding.TargetKind);

        var serviceFinding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
        Assert.Equal("api", serviceFinding.Target);

        Assert.Single(report.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);

        Assert.Equal(7, report.Findings.Count);
    }
}
