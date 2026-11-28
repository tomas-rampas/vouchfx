// Vouchfx.Engine.Authoring — YamlDepthGuard: refuses YAML nested deeper than the limit before
// YamlStream.Load can overflow the stack on it. The scan and the diagnostic text are in
// YamlDepthGuard.Scan.cs.

namespace Vouchfx.Engine.Authoring;

internal static partial class YamlDepthGuard
{
    /// <summary>
    /// Throws <see cref="YamlParseException"/> when <paramref name="text"/> holds a collection
    /// nested deeper than <see cref="MaxDepth"/> (see <see cref="TryFindExcessDepth"/>).
    /// </summary>
    /// <param name="text">The YAML text about to be loaded.</param>
    /// <exception cref="YamlParseException">The text is nested too deeply.</exception>
    internal static void Check(string text)
    {
        if (TryFindExcessDepth(text, out var refusal))
        {
            throw new YamlParseException(refusal.Message(), refusal.Line, refusal.Column);
        }
    }
}
