// Platform.Engine.Authoring — YamlParseException (S03-B-01).
//
// Thrown by YamlDocumentParser when the input cannot be deserialised into an
// E2eDocument.  Carries optional line/column information for diagnostics.

namespace Platform.Engine.Authoring;

/// <summary>
/// Thrown when <see cref="YamlDocumentParser.Parse"/> fails to deserialise the
/// input into a valid <see cref="Model.E2eDocument"/>.
/// </summary>
/// <remarks>
/// Covers three categories of failure:
/// <list type="bullet">
///   <item><description>Malformed YAML (underlying YamlDotNet parse error).</description></item>
///   <item><description>Empty or whitespace-only input.</description></item>
///   <item><description>Structurally unexpected YAML (e.g. the root node is not a mapping).</description></item>
/// </list>
/// <para>
/// <see cref="Line"/> and <see cref="Column"/> are 1-based positions sourced
/// from <c>YamlNode.Start</c> (a <c>Mark</c>) when available; both are zero
/// when position information is not present.
/// </para>
/// </remarks>
public sealed class YamlParseException : Exception
{
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
    /// Initialises a new <see cref="YamlParseException"/> without position information.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    public YamlParseException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="YamlParseException"/> without position information,
    /// wrapping an inner exception from the underlying YAML parser.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    /// <param name="innerException">The underlying YamlDotNet exception.</param>
    public YamlParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="YamlParseException"/> with source position information.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    public YamlParseException(string message, long line, long column)
        : base(message)
    {
        Line = line;
        Column = column;
    }

    /// <summary>
    /// Initialises a new <see cref="YamlParseException"/> with source position
    /// information and an inner exception.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <param name="innerException">The underlying YamlDotNet exception.</param>
    public YamlParseException(string message, long line, long column, Exception innerException)
        : base(message, innerException)
    {
        Line = line;
        Column = column;
    }
}
