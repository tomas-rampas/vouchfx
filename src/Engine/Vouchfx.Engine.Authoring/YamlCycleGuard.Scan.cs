// Vouchfx.Engine.Authoring — YamlCycleGuard.Scan: the event-level self-reference pre-scan.
//
// This file is deliberately free of any Authoring type so that the Compilation and
// Orchestration projects, which load YAML on their own, can compile the very same source
// through <Compile Include ... Link> (the HttpRestBodyConversion precedent) without
// referencing Authoring's internals. The throwing wrapper lives in YamlCycleGuard.cs.

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vouchfx.Engine.Authoring;

internal static partial class YamlCycleGuard
{
    /// <summary>
    /// Finds the first alias that names a still-open anchor from inside a mapping key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// YamlDotNet 16.3.0 <c>YamlStream.Load</c> hashes a mapping key when it inserts it. A
    /// key that is a collection containing an alias to its own still-open anchor
    /// (<c>? &amp;k [ *k ] : v</c>) therefore recurses without bound and overflows the stack,
    /// which cannot be caught. The same self-reference outside a key
    /// (<c>a: &amp;k [ *k ]</c>) is never hashed and loads as an ordinary cyclic graph, so it
    /// is not refused here. Complex (non-scalar) keys stay legal; only the self-reference
    /// inside one is.
    /// </para>
    /// <para>
    /// The walk is iterative and O(n): one pass over the parser events with an explicit stack
    /// of open containers. A malformed document ends the scan quietly (returns false) so that
    /// <c>YamlStream.Load</c> reports the syntax error with its own message.
    /// </para>
    /// </remarks>
    /// <param name="text">The YAML text.</param>
    /// <param name="anchor">The alias name that refers to an open node.</param>
    /// <param name="line">1-based line of the alias.</param>
    /// <param name="column">1-based column of the alias.</param>
    /// <returns><see langword="true"/> when a self-referencing key was found.</returns>
    internal static bool TryFindSelfReferencingKey(string text, out string anchor, out long line, out long column)
    {
        anchor = string.Empty;
        line = 0;
        column = 0;

        // Latest definition of each anchor name: true while that node is still open. A
        // later definition of the same name shadows the earlier one, as it does for aliases.
        var open = new Dictionary<string, bool>(StringComparer.Ordinal);
        var frames = new Stack<Frame>();

        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                var ev = parser.Current;
                switch (ev)
                {
                    case MappingStart or SequenceStart:
                        {
                            var node = (NodeEvent)ev;
                            var inKey = EnterChild(frames);
                            var name = node.Anchor.IsEmpty ? null : node.Anchor.Value;
                            if (name is not null)
                            {
                                open[name] = true;
                            }

                            frames.Push(new Frame(ev is MappingStart, name, inKey));
                            break;
                        }

                    case MappingEnd or SequenceEnd:
                        {
                            if (frames.Count > 0)
                            {
                                var frame = frames.Pop();
                                if (frame.Anchor is not null)
                                {
                                    open[frame.Anchor] = false;
                                }
                            }

                            break;
                        }

                    case Scalar scalar:
                        {
                            EnterChild(frames);
                            if (!scalar.Anchor.IsEmpty)
                            {
                                open[scalar.Anchor.Value] = false;
                            }

                            break;
                        }

                    case AnchorAlias alias:
                        {
                            var inKey = EnterChild(frames);
                            if (inKey && open.TryGetValue(alias.Value.Value, out var isOpen) && isOpen)
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
        public Frame(bool isMapping, string? anchor, bool inKey)
        {
            IsMapping = isMapping;
            Anchor = anchor;
            InKey = inKey;
        }

        public bool IsMapping { get; }

        public string? Anchor { get; }

        public bool InKey { get; }

        public int Children { get; set; }
    }
}
