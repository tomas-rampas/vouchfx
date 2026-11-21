// Vouchfx.Engine.Authoring — YamlKeyAliasGuard.Scan: the event-level pre-scan for aliases to
// collections inside mapping keys, and the diagnostic text for a hit.
//
// This file is deliberately free of any Authoring type so that the Compilation and
// Orchestration projects, which load YAML on their own, can compile the very same source
// through <Compile Include ... Link> (the HttpRestBodyConversion precedent) without
// referencing Authoring's internals. The throwing wrapper lives in YamlKeyAliasGuard.cs.

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vouchfx.Engine.Authoring;

internal static partial class YamlKeyAliasGuard
{
    private const int MaxEchoedAliasLength = 64;

    /// <summary>
    /// Finds the first alias inside a mapping key whose target anchor sits on a mapping or a
    /// sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// YamlDotNet 16.3.0 <c>YamlStream.Load</c> hashes a mapping key when it inserts it, and
    /// the hash of a sequence recurses through every element with no depth limit and no
    /// memoisation. A key that reaches a cyclic, very deep or heavily shared collection
    /// through an alias therefore overflows the stack (which cannot be caught), or costs time
    /// exponential in the sharing depth. Rather than detect each such shape, the rule makes
    /// key hashing bounded by construction: inside a key, at any depth, an alias may name only
    /// a scalar anchor. A collection written inline in a key (<c>? [a, b] : v</c>) is legal;
    /// so is every alias outside a key, including a self-referencing value
    /// (<c>a: &amp;k [ *k ]</c>), which is never hashed.
    /// </para>
    /// <para>
    /// The kind of an anchor follows YAML: the most recent definition of a name wins, and
    /// anchors do not carry across documents. The walk is iterative and O(n): one pass over
    /// the parser events with an explicit stack of open containers. A malformed document
    /// ends the scan quietly (returns false) so that <c>YamlStream.Load</c> reports the syntax
    /// error with its own message.
    /// </para>
    /// </remarks>
    /// <param name="text">The YAML text.</param>
    /// <param name="anchor">The alias name that refers to a collection.</param>
    /// <param name="line">1-based line of the alias.</param>
    /// <param name="column">1-based column of the alias.</param>
    /// <returns><see langword="true"/> when such an alias was found.</returns>
    internal static bool TryFindCollectionAliasInKey(string text, out string anchor, out long line, out long column)
    {
        anchor = string.Empty;
        line = 0;
        column = 0;

        // Latest definition of each anchor name: true when that node is a mapping or a
        // sequence. A collection's anchor is recorded when the collection STARTS, so an alias
        // to a still-open anchor reads as a collection too.
        var isCollection = new Dictionary<string, bool>(StringComparer.Ordinal);
        var frames = new Stack<Frame>();

        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                var ev = parser.Current;
                switch (ev)
                {
                    case DocumentStart:
                        isCollection.Clear();
                        frames.Clear();
                        break;

                    case MappingStart or SequenceStart:
                        {
                            var node = (NodeEvent)ev;
                            var inKey = EnterChild(frames);
                            if (!node.Anchor.IsEmpty)
                            {
                                isCollection[node.Anchor.Value] = true;
                            }

                            frames.Push(new Frame(ev is MappingStart, inKey));
                            break;
                        }

                    case MappingEnd or SequenceEnd:
                        {
                            if (frames.Count > 0)
                            {
                                frames.Pop();
                            }

                            break;
                        }

                    case Scalar scalar:
                        {
                            EnterChild(frames);
                            if (!scalar.Anchor.IsEmpty)
                            {
                                isCollection[scalar.Anchor.Value] = false;
                            }

                            break;
                        }

                    case AnchorAlias alias:
                        {
                            var inKey = EnterChild(frames);
                            if (inKey && isCollection.TryGetValue(alias.Value.Value, out var collection) && collection)
                            {
                                anchor = alias.Value.Value;
                                line = alias.Start.Line;
                                column = alias.Start.Column;
                                return true;
                            }

                            break;
                        }
                }
            }
        }
        catch (YamlException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Builds the diagnostic for a refused key alias (ASCII only; long names are truncated).</summary>
    internal static string Message(string anchor, long line, long column) =>
        $"YAML parse error at {Detail(anchor, line, column)}";

    /// <summary>
    /// The location and cause of a refused key alias, without the "YAML parse error at" prefix,
    /// for callers that wrap it in their own message.
    /// </summary>
    internal static string Detail(string anchor, long line, long column)
    {
        var echoed = anchor.Length > MaxEchoedAliasLength
            ? string.Concat(anchor.AsSpan(0, MaxEchoedAliasLength), "...")
            : anchor;
        return $"line {line}, column {column}: alias '{echoed}' inside a mapping key refers to a " +
               "mapping or sequence; a key may refer by alias only to a scalar.";
    }

    // Registers a new child node under the current container and reports whether that
    // child sits inside a mapping key (it is a key itself, or nested within one).
    private static bool EnterChild(Stack<Frame> frames)
    {
        if (frames.Count == 0)
        {
            return false;
        }

        var parent = frames.Peek();
        var isKey = parent.InKey || (parent.IsMapping && parent.Children % 2 == 0);
        parent.Children++;
        return isKey;
    }

    private sealed class Frame
    {
        public Frame(bool isMapping, bool inKey)
        {
            IsMapping = isMapping;
            InKey = inKey;
        }

        public bool IsMapping { get; }

        public bool InKey { get; }

        public int Children { get; set; }
    }
}
