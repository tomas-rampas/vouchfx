// MetricsAssertPrometheusModel — strongly-typed binding model for the
// metrics-assert.prometheus step kind (DSL §5, brand-new metrics-assert family).
//
// DEVIATION FROM THE ORIGINAL PLAN SKETCH (documented, see the provider's file header
// for the full rationale): the plan sketch described a single required "url" field
// (an absolute URL template).  There is no mechanism in the current engine for an
// author to resolve an arbitrary absolute-URL {placeholder} against a dynamically
// assigned Aspire endpoint port outside the target/svc:: staging path that http.rest
// already uses — environment.services entries are staged ONLY at svc::<name> (read via
// VarKeys.Service), never at a plain Vars[<name>] entry (that dual-staging is unique to
// webhook-listen's host resources).  This provider therefore uses the SAME Target
// (service name) + Path shape as http.rest instead of a raw Url: Target names a service
// declared under environment.services (the SUT, per the family's stated intent — "the
// SUT's own /metrics"), and Path defaults to "/metrics" when omitted.  This requires no
// new engine plumbing and honours the family's "no infrastructure" design point exactly
// as http.rest does (no environment.dependencies entry — the target is a service, not a
// managed dependency).
using Platform.Sdk;

namespace Platform.Steps.MetricsAssert.Prometheus;

/// <summary>
/// The value assertion block for a <c>metrics-assert.prometheus</c> step.  At least one
/// member must be set; all three may combine.
/// </summary>
/// <param name="Value">
/// Raw template string for the expected exact value (parsed as <see cref="double"/> at
/// step-execution time).  Exact numeric equality is evaluated with a relative tolerance
/// of <c>1e-9</c> (see <c>MetricsAssertPrometheus_Helpers</c>).  May contain
/// <c>{placeholder}</c> / <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Min">
/// Raw template string for the inclusive lower bound (parsed as <see cref="double"/> at
/// step-execution time).  May contain <c>{placeholder}</c> / <c>${secret:source/path}</c>
/// tokens.
/// </param>
/// <param name="Max">
/// Raw template string for the inclusive upper bound (parsed as <see cref="double"/> at
/// step-execution time).  May contain <c>{placeholder}</c> / <c>${secret:source/path}</c>
/// tokens.
/// </param>
public sealed record MetricsExpectation(
    string? Value,
    string? Min,
    string? Max);

/// <summary>
/// Strongly-typed model for the <c>metrics-assert.prometheus</c> step kind.
/// </summary>
/// <param name="Target">
/// Logical name of the service to scrape, as declared under
/// <c>environment.services</c> — normally the system under test.  Resolved to a real
/// address by Aspire service discovery at orchestration time (mirrors
/// <c>HttpRestModel.Target</c> exactly).
/// </param>
/// <param name="Path">
/// The scrape path, which may contain <c>{placeholder}</c> / <c>${secret:source/path}</c>
/// tokens resolved at step-execution time.  Defaults to <c>/metrics</c> when the author
/// omits the field (bound at <see cref="MetricsAssertPrometheusProvider.Bind"/> time).
/// </param>
/// <param name="Metric">
/// The Prometheus sample (metric) name to select, e.g. <c>orders_processed_total</c>.
/// May contain <c>{placeholder}</c> / <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Labels">
/// Optional map of required label name → expected value.  A sample matches only when
/// it carries every declared label with exactly the expected value (a label subset
/// match — the sample may carry additional labels not mentioned here).  Values may
/// contain <c>{placeholder}</c> / <c>${secret:source/path}</c> tokens.
/// <see langword="null"/> when no label criteria are declared.
/// </param>
/// <param name="Expect">
/// The value assertion block.  At least one of <see cref="MetricsExpectation.Value"/>,
/// <see cref="MetricsExpectation.Min"/>, or <see cref="MetricsExpectation.Max"/> must be
/// set (enforced by <see cref="MetricsAssertPrometheusProvider.Validate"/>).
/// </param>
public sealed record MetricsAssertPrometheusModel(
    string Target,
    string Path,
    string Metric,
    IReadOnlyDictionary<string, string>? Labels,
    MetricsExpectation Expect) : IStepModel;
