// Vouchfx.Engine.Authoring — YamlCycleGuard: refuses a self-referencing mapping key before
// YamlStream.Load can overflow the stack on it. The scan itself is in YamlCycleGuard.Scan.cs.

namespace Vouchfx.Engine.Authoring;

internal static partial class YamlCycleGuard
{
    /// <summary>
    /// Throws <see cref="YamlParseException"/> when <paramref name="text"/> holds a mapping key
    /// that contains an alias to its own still-open anchor.
    /// </summary>
    /// <param name="text">The YAML text about to be loaded.</param>
    /// <exception cref="YamlParseException">A self-referencing key was found.</exception>
    internal static void Check(string text)
    {
        if (TryFindSelfReferencingKey(text, out var anchor, out var line, out var column))
        {
            throw new YamlParseException(Message(anchor, line, column), line, column);
        }
    }

    /// <summary>Builds the diagnostic for a self-referencing key (ASCII only).</summary>
    internal static string Message(string anchor, long line, long column) =>
        $"YAML parse error at line {line}, column {column}: alias '{anchor}' refers to a node that is still being defined " +
        "(a mapping or sequence that contains itself); self-referencing nodes are not supported.";
}
