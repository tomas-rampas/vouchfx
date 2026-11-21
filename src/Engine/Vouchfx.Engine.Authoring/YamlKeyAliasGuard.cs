// Vouchfx.Engine.Authoring — YamlKeyAliasGuard: refuses an alias to a collection inside a
// mapping key before YamlStream.Load can hash it without bound. The scan and the diagnostic
// text are in YamlKeyAliasGuard.Scan.cs.

namespace Vouchfx.Engine.Authoring;

internal static partial class YamlKeyAliasGuard
{
    /// <summary>
    /// Throws <see cref="YamlParseException"/> when <paramref name="text"/> holds a mapping key
    /// that contains an alias to a mapping or a sequence.
    /// </summary>
    /// <param name="text">The YAML text about to be loaded.</param>
    /// <exception cref="YamlParseException">A refused key alias was found.</exception>
    internal static void Check(string text)
    {
        if (TryFindCollectionAliasInKey(text, out var anchor, out var line, out var column))
        {
            throw new YamlParseException(Message(anchor, line, column), line, column);
        }
    }
}
