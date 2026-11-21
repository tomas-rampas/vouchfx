// Vouchfx.Engine.Authoring — YamlKeyAliasGuard.Scan: the event-level pre-scan for aliases
// inside mapping keys that YamlStream.Load cannot hash in bounded time, and the diagnostic
// text for a hit.
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
    /// <summary>
    /// The most scalar text, in UTF-16 code units, that aliases inside mapping keys may bring
    /// into key hashing across one document. Each alias is charged the length of its scalar
    /// once for every mapping key that encloses it, because <c>YamlStream.Load</c> hashes a
    /// nested key again as part of each enclosing key.
    /// </summary>
    internal const long MaxKeyAliasText = 1_048_576;

    private const int MaxEchoedAliasLength = 64;

    // The recorded kind of an anchor whose node is a mapping or a sequence. A scalar anchor
    // records its value's length instead, which is never negative.
    private const int CollectionAnchor = -1;

    /// <summary>Why an alias inside a mapping key was refused.</summary>
    internal enum RefusalKind
    {
        /// <summary>The alias names an anchor on a mapping or a sequence.</summary>
        CollectionAnchor,

        /// <summary>The alias names an anchor not defined earlier in the same document.</summary>
        AnchorNotDefinedBefore,

        /// <summary>The alias takes the scalar text repeated in keys past <see cref="MaxKeyAliasText"/>.</summary>
        KeyAliasTextBudget,
    }

    /// <summary>
    /// Finds the first alias inside a mapping key that <c>YamlStream.Load</c> could not hash in
    /// time bounded by the document's length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// YamlDotNet 16.3.0 <c>YamlStream.Load</c> hashes a mapping key when it inserts it, and
    /// the hash of a collection recurses through every element with no depth limit and no
    /// memoisation. Inside a key, at any depth, an alias is therefore legal only when its
    /// anchor is already defined in the current document and sits on a scalar:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// An alias to a mapping or a sequence lets a key reach a cyclic, very deep or heavily
    /// shared collection, which overflows the stack (uncatchable) or costs time exponential in
    /// the sharing depth. A collection's anchor is recorded when the collection starts, so an
    /// alias to a still-open anchor (a self-reference) counts as a collection too.
    /// </description></item>
    /// <item><description>
    /// An alias whose anchor is not defined before it (a forward alias, or a name never
    /// defined in this document) is loaded as an unresolved placeholder and resolved at the
    /// end of the document. A placeholder used as a key recurses in its own equality check,
    /// whatever its target; a placeholder nested in a key is hashed again once resolved.
    /// YAML requires an anchor to precede its alias, so no valid document is refused.
    /// </description></item>
    /// <item><description>
    /// An alias to a scalar is hashed over the scalar's full text at every use, so a long
    /// scalar repeated through many keys costs time quadratic in the document's length. The
    /// text aliases bring into keys is capped at <see cref="MaxKeyAliasText"/> per document.
    /// </description></item>
    /// </list>
    /// <para>
    /// With these rules every alias in a key resolves at once to a scalar, and the cost of
    /// hashing keys is bounded by the key text written in the document plus that cap. A
    /// collection written inline in a key (<c>? [a, b] : v</c>) stays legal, as does every
    /// alias outside a key, including a forward or self-referencing value
    /// (<c>a: &amp;k [ *k ]</c>), which is never hashed.
    /// </para>
    /// <para>
    /// The kind of an anchor follows YAML and YamlDotNet: the most recent definition of a name
    /// wins, and anchors do not carry across documents. The walk is iterative and O(n): one
    /// pass over the parser events with an explicit stack of open containers. A malformed
    /// document ends the scan quietly (returns false) so that <c>YamlStream.Load</c> reports
    /// the syntax error with its own message.
    /// </para>
    /// </remarks>
    /// <param name="text">The YAML text.</param>
    /// <param name="refusal">The refused alias, its location and the reason.</param>
    /// <returns><see langword="true"/> when such an alias was found.</returns>
    internal static bool TryFindRefusedKeyAlias(string text, out Refusal refusal)
    {
        refusal = default;

        // Latest definition of each anchor name in the current document: CollectionAnchor for
        // a mapping or a sequence, otherwise the scalar's length.
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var frames = new Stack<Frame>();
        long keyAliasText = 0;

        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                var ev = parser.Current;
                switch (ev)
                {
                    case DocumentStart:
                        anchors.Clear();
                        frames.Clear();
                        keyAliasText = 0;
                        break;

                    case MappingStart or SequenceStart:
                        {
                            var node = (NodeEvent)ev;
                            var keyDepth = EnterChild(frames);
                            if (!node.Anchor.IsEmpty)
                            {
                                anchors[node.Anchor.Value] = CollectionAnchor;
                            }

                            frames.Push(new Frame(ev is MappingStart, keyDepth));
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
                                anchors[scalar.Anchor.Value] = scalar.Value.Length;
                            }

                            break;
                        }

                    case AnchorAlias alias:
                        {
                            var keyDepth = EnterChild(frames);
                            if (keyDepth == 0)
                            {
                                break;
                            }

                            var name = alias.Value.Value;
                            RefusalKind? kind = null;
                            if (!anchors.TryGetValue(name, out var length))
                            {
                                kind = RefusalKind.AnchorNotDefinedBefore;
                            }
                            else if (length == CollectionAnchor)
                            {
                                kind = RefusalKind.CollectionAnchor;
                            }
                            else
                            {
                                keyAliasText += (long)length * keyDepth;
                                if (keyAliasText > MaxKeyAliasText)
                                {
                                    kind = RefusalKind.KeyAliasTextBudget;
                                }
                            }

                            if (kind is { } refused)
                            {
                                refusal = new Refusal(refused, name, alias.Start.Line, alias.Start.Column);
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
    internal static string Message(RefusalKind kind, string anchor, long line, long column) =>
        $"YAML parse error at {Detail(kind, anchor, line, column)}";

    /// <summary>
    /// The location and cause of a refused key alias, without the "YAML parse error at" prefix,
    /// for callers that wrap it in their own message.
    /// </summary>
    internal static string Detail(RefusalKind kind, string anchor, long line, long column)
    {
        var where = $"line {line}, column {column}: alias '{Echo(anchor)}' inside a mapping key ";
        return kind switch
        {
            RefusalKind.AnchorNotDefinedBefore =>
                where + "refers to an anchor that is not defined before it; a key may refer by alias " +
                "only to a scalar anchor defined earlier in the same document.",
            RefusalKind.KeyAliasTextBudget =>
                where + $"brings the scalar text repeated through aliases in keys past {MaxKeyAliasText} " +
                "characters (a key nested in another key counts again); shorten the aliased scalars or " +
                "write fewer such keys.",
            _ =>
                where + "refers to a mapping or sequence; a key may refer by alias only to a scalar.",
        };
    }

    // Caps an echoed alias name, backing off rather than splitting a surrogate pair.
    private static string Echo(string anchor)
    {
        if (anchor.Length <= MaxEchoedAliasLength)
        {
            return anchor;
        }

        var cut = MaxEchoedAliasLength;
        while (cut > 0 && char.IsHighSurrogate(anchor[cut - 1]))
        {
            cut--;
        }

        return string.Concat(anchor.AsSpan(0, cut), "...");
    }

    // Registers a new child node under the current container and returns the number of
    // mapping keys that enclose it, counting the child itself when it is a key (0 when it sits
    // in no key at all).
    private static int EnterChild(Stack<Frame> frames)
    {
        if (frames.Count == 0)
        {
            return 0;
        }

        var parent = frames.Peek();
        var keyDepth = parent.KeyDepth + (parent.IsMapping && parent.Children % 2 == 0 ? 1 : 0);
        parent.Children++;
        return keyDepth;
    }

    /// <summary>A refused alias inside a mapping key.</summary>
    /// <param name="Kind">Why it was refused.</param>
    /// <param name="Anchor">The alias name as written.</param>
    /// <param name="Line">1-based line of the alias.</param>
    /// <param name="Column">1-based column of the alias.</param>
    internal readonly record struct Refusal(RefusalKind Kind, string Anchor, long Line, long Column)
    {
        /// <summary>The full diagnostic, starting "YAML parse error at".</summary>
        public string Message => YamlKeyAliasGuard.Message(Kind, Anchor, Line, Column);

        /// <summary>The diagnostic without the "YAML parse error at" prefix.</summary>
        public string Detail => YamlKeyAliasGuard.Detail(Kind, Anchor, Line, Column);
    }

    private sealed class Frame
    {
        public Frame(bool isMapping, int keyDepth)
        {
            IsMapping = isMapping;
            KeyDepth = keyDepth;
        }

        public bool IsMapping { get; }

        public int KeyDepth { get; }

        public int Children { get; set; }
    }
}
