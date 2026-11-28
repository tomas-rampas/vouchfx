// Vouchfx.Engine.Authoring — YamlDepthGuard.Scan: the event-level pre-scan that refuses YAML
// nested deeper than MaxDepth collections, and the diagnostic text for a hit.
//
// This file is deliberately free of any Authoring type so that the Compilation and
// Orchestration projects, which load YAML on their own, can compile the very same source
// through <Compile Include ... Link> (the YamlKeyAliasGuard.Scan precedent) without
// referencing Authoring's internals. The throwing wrapper lives in YamlDepthGuard.cs.

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vouchfx.Engine.Authoring;

internal static partial class YamlDepthGuard
{
    /// <summary>
    /// The most collections (sequences and mappings, block or flow) that may be open at once in
    /// one YAML document. The collection that would be the next one is refused, whether or not
    /// it has content.
    /// </summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// Finds the first collection that would be open beyond <see cref="MaxDepth"/> levels deep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>YamlStream.Load</c> and YamlDotNet's deserialiser recurse once per nested collection,
    /// so a document nested a few tens of thousands of levels deep overflows the stack, which
    /// no catch can stop. The scan reads parser events only, keeps a counter instead of a
    /// stack, and returns at the first collection past the limit, so the work it does for a
    /// refused document depends on the limit and on the text up to that collection, never on
    /// the document's length. A document that stays within the limit is read to its end.
    /// </para>
    /// <para>
    /// The depth is counted per document and starts again at each <c>---</c>. Aliases are not
    /// followed: <c>YamlStream.Load</c> and the deserialiser bind an alias to the node already
    /// built for its anchor, so the load recurses no deeper than the written nesting. The loaded
    /// graph can still be far deeper than the text (each anchored sequence holding an alias to
    /// the one before), so a walk that follows aliases needs its own bound; the schema bridge's
    /// serialiser stops at its recursion limit. A malformed document ends the scan
    /// quietly (returns false), whatever the scanner throws for it, so that the load which
    /// follows reports the failure as it would without the scan.
    /// </para>
    /// </remarks>
    /// <param name="text">The YAML text.</param>
    /// <param name="refusal">The first collection past the limit and its location.</param>
    /// <returns><see langword="true"/> when such a collection was found.</returns>
    internal static bool TryFindExcessDepth(string text, out DepthRefusal refusal)
    {
        refusal = default;
        var depth = 0;

        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case DocumentStart:
                        depth = 0;
                        break;

                    case MappingStart or SequenceStart:
                        depth++;
                        if (depth > MaxDepth)
                        {
                            var start = parser.Current!.Start;
                            refusal = new DepthRefusal(start.Line, start.Column);
                            return true;
                        }

                        break;

                    case MappingEnd or SequenceEnd:
                        if (depth > 0)
                        {
                            depth--;
                        }

                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException
            or TypeInitializationException))
        {
            // A malformed document. Besides YamlException, YamlDotNet 16.3.0's scanner throws
            // ArgumentOutOfRangeException for a \U escape above U+10FFFF, ArgumentNullException
            // for a %TAG directive with an empty prefix and ArgumentException for an empty
            // verbatim tag (measured). Every such failure belongs to the load that follows, which
            // meets the same text. The exclusions follow YamlKeyAliasGuard.TryFindRefusedKeyAlias.
            return false;
        }

        return false;
    }

    /// <summary>Builds the diagnostic for a refused document (ASCII only).</summary>
    internal static string Message(long line, long column) =>
        $"YAML parse error at {Detail(line, column)}";

    /// <summary>
    /// The location and cause of a refused document, without the "YAML parse error at" prefix,
    /// for callers that wrap it in their own message.
    /// </summary>
    internal static string Detail(long line, long column) =>
        $"line {line}, column {column}: this collection is nested more than {MaxDepth} levels deep; " +
        $"the limit is {MaxDepth} nested collections (sequences and mappings, block or flow, counted " +
        "in each document). Flatten the structure.";

    /// <summary>The first collection past the nesting limit.</summary>
    /// <param name="Line">1-based line of the collection's first character.</param>
    /// <param name="Column">1-based column of the collection's first character.</param>
    internal readonly record struct DepthRefusal(long Line, long Column)
    {
        /// <summary>The full diagnostic, starting "YAML parse error at".</summary>
        /// <returns>The diagnostic text.</returns>
        public string Message() => YamlDepthGuard.Message(Line, Column);

        /// <summary>The diagnostic without the "YAML parse error at" prefix.</summary>
        /// <returns>The diagnostic text.</returns>
        public string Detail() => YamlDepthGuard.Detail(Line, Column);
    }
}
