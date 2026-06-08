// Platform.Engine.Authoring — AstBuildException (S03-B-02).
//
// Thrown by AstBuilder when a step cannot be normalised into the typed AST.
// Carries the step identifier and the 1-based source position from the YAML
// node so that diagnostic messages name the author-visible location.

namespace Platform.Engine.Authoring;

/// <summary>
/// Thrown when <see cref="AstBuilder.Build"/> is unable to normalise a step
/// into the typed AST, for example when the step type is unknown, ambiguous,
/// or uses an illegal bare-family alias.
/// </summary>
/// <remarks>
/// <para>
/// The exception message is formatted as
/// <c>"(line N, col C): step 'id': &lt;reason&gt;"</c> so that it is immediately
/// actionable in CI output and IDE terminals without requiring the caller to
/// inspect the structured properties.
/// </para>
/// <para>
/// <see cref="Line"/> and <see cref="Column"/> are 1-based positions sourced
/// from <c>YamlNode.Start</c> (a YamlDotNet <c>Mark</c>) when available; both
/// are zero when position information is not present.
/// </para>
/// </remarks>
public sealed class AstBuildException : Exception
{
    /// <summary>
    /// The identifier of the step that triggered the failure, or
    /// <see langword="null"/> when the error is not attributable to a single step.
    /// </summary>
    public string? StepId { get; }

    /// <summary>
    /// 1-based line number in the source YAML where the error was detected,
    /// or zero when position information is unavailable.
    /// </summary>
    /// <remarks>
    /// Stored as <see cref="long"/> to match the <c>Mark.Line</c> type exposed
    /// by YamlDotNet 16+.
    /// </remarks>
    public long Line { get; }

    /// <summary>
    /// 1-based column number in the source YAML where the error was detected,
    /// or zero when position information is unavailable.
    /// </summary>
    /// <remarks>
    /// Stored as <see cref="long"/> to match the <c>Mark.Column</c> type exposed
    /// by YamlDotNet 16+.
    /// </remarks>
    public long Column { get; }

    /// <summary>
    /// Initialises a new <see cref="AstBuildException"/> with full location
    /// context.
    /// </summary>
    /// <param name="stepId">
    /// The identifier of the offending step, or <see langword="null"/> when not
    /// attributable to a specific step.
    /// </param>
    /// <param name="line">1-based line number from the step's YAML node.</param>
    /// <param name="column">1-based column number from the step's YAML node.</param>
    /// <param name="reason">
    /// Human-readable explanation of what went wrong, without location prefix
    /// (the constructor adds the <c>"(line N, col C): step 'id': "</c> prefix).
    /// </param>
    public AstBuildException(string? stepId, long line, long column, string reason)
        : base(BuildMessage(stepId, line, column, reason))
    {
        StepId = stepId;
        Line = line;
        Column = column;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static string BuildMessage(string? stepId, long line, long column, string reason)
    {
        var location = (line > 0 || column > 0)
            ? $"(line {line}, col {column}): "
            : string.Empty;

        var stepPart = stepId is not null
            ? $"step '{stepId}': "
            : string.Empty;

        return $"{location}{stepPart}{reason}";
    }
}
