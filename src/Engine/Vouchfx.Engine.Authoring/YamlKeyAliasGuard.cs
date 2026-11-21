// Vouchfx.Engine.Authoring — YamlKeyAliasGuard: refuses an alias inside a mapping key that
// YamlStream.Load could not hash in bounded time (an alias to a collection, an alias whose
// anchor is not defined before it, or scalar text past the key-alias budget). The scan and
// the diagnostic text are in YamlKeyAliasGuard.Scan.cs.

namespace Vouchfx.Engine.Authoring;

internal static partial class YamlKeyAliasGuard
{
    /// <summary>
    /// Throws <see cref="YamlParseException"/> when <paramref name="text"/> holds a mapping key
    /// that contains a refused alias (see <see cref="TryFindRefusedKeyAlias"/>).
    /// </summary>
    /// <param name="text">The YAML text about to be loaded.</param>
    /// <exception cref="YamlParseException">A refused key alias was found.</exception>
    internal static void Check(string text)
    {
        if (TryFindRefusedKeyAlias(text, out var refusal))
        {
            throw new YamlParseException(refusal.Message, refusal.Line, refusal.Column);
        }
    }
}
