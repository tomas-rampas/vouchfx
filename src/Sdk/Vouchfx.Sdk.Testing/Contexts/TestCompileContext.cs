// Vouchfx.Sdk.Testing — TestCompileContext.
//
// A public, ready-to-use implementation of the frozen Vouchfx.Sdk ICompileContext,
// supplied to a provider's Emit stage when driving it through the harness.  It mirrors
// the engine's RunCompileContext exactly: it is constructed from the format-aware
// CaptureExpr map and derives the back-compatible expression-string Captures view from
// it, so the two views stay key- and value-aligned (the engine's invariant).
using Vouchfx.Sdk;

namespace Vouchfx.Sdk.Testing.Contexts;

/// <summary>
/// A public, reusable implementation of <see cref="ICompileContext"/> for driving a
/// provider's compilation (emit) stage in a test harness.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICompileContext"/> is an engine-supplied, provider-consumed interface
/// (CLAUDE.md §13) carrying the step identity (<see cref="StepId"/>,
/// <see cref="SuiteNamespace"/>) and the step's <c>capture</c> map.  In production the
/// engine's provider pipeline constructs it per step; the harness supplies this
/// stand-in so a provider's <c>Emit</c> can be exercised in isolation.
/// </para>
/// <para>
/// This stub mirrors the engine's runtime context: it is constructed from the
/// format-aware <see cref="CaptureExprs"/> map and projects the back-compatible
/// expression-string <see cref="Captures"/> view from it (order-preserving), so a
/// provider that reads either view sees identical keys and aligned values — exactly the
/// behaviour the engine guarantees.
/// </para>
/// </remarks>
public sealed class TestCompileContext : ICompileContext
{
    private static readonly IReadOnlyDictionary<string, CaptureExpr> s_emptyExprs =
        new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> s_emptyStrings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Initialises a new <see cref="TestCompileContext"/> for the given step.
    /// </summary>
    /// <param name="stepId">
    /// The identifier of the step being compiled.  Providers must sanitise this via
    /// <see cref="CsxFragment.SanitiseId"/> before embedding it in emitted variable
    /// names.  Must not be <see langword="null"/>.
    /// </param>
    /// <param name="suiteNamespace">
    /// The C# namespace into which the compiled suite is emitted.  Defaults to
    /// <c>"VouchfxGenerated"</c>, matching the engine's default suite namespace.
    /// </param>
    /// <param name="captureExprs">
    /// The step's <c>capture</c> map (variable name → typed extractor).  Pass
    /// <see langword="null"/> (or omit) to use an empty map — the default for a step
    /// with no <c>capture</c> section.  The back-compatible <see cref="Captures"/> view
    /// is derived from this, preserving iteration order.
    /// </param>
    public TestCompileContext(
        string stepId,
        string suiteNamespace = "VouchfxGenerated",
        IReadOnlyDictionary<string, CaptureExpr>? captureExprs = null)
    {
        ArgumentNullException.ThrowIfNull(stepId);

        StepId = stepId;
        SuiteNamespace = suiteNamespace;
        CaptureExprs = captureExprs ?? s_emptyExprs;
        Captures = ProjectExpressions(CaptureExprs);
    }

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Captures { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }

    /// <summary>
    /// Projects a format-aware capture map to the back-compatible expression-string
    /// view, preserving iteration order so the two views stay key- and value-aligned
    /// (mirrors the engine's <c>RunCompileContext</c> projection).
    /// </summary>
    private static IReadOnlyDictionary<string, string> ProjectExpressions(
        IReadOnlyDictionary<string, CaptureExpr> exprs)
    {
        if (exprs.Count == 0)
        {
            return s_emptyStrings;
        }

        var projected = new Dictionary<string, string>(exprs.Count, StringComparer.Ordinal);
        foreach (var (name, expr) in exprs)
        {
            projected[name] = expr.Expression;
        }

        return projected;
    }
}
