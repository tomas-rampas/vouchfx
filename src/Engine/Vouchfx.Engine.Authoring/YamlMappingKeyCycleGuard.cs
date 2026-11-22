// Refuses a YAML mapping key whose hash graph contains a cycle before YamlStream.Load.
//
// YamlMappingNode.Load inserts each key into an OrderedDictionary, and that insert
// hashes the key. YamlSequenceNode.GetHashCode walks every child with no visited set.
// YamlMappingNode.GetHashCode walks every key, and a value only when that value has
// no anchor (an anchored value contributes its anchor name, not its graph). An alias
// whose anchor is already registered is the node itself, and a node registers its
// anchor at the start of the node, before its children are read. A sequence key that
// contains an alias to itself therefore hashes itself until the process stack
// overflows. StackOverflowException is not catchable.
//
// This walk follows those same edges and stops on a repeated node. It does not refuse
// a cycle that lives only in a value, a merge key, or an anchor that is redefined
// before the alias: those documents load, because the hash never follows the edge.
// The scan is iterative. A desync fails closed, so a document this walk cannot follow
// is refused rather than handed to Load.

using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;

namespace Vouchfx.Engine.YamlSafety;

internal static class YamlMappingKeyCycleGuard
{
    internal static void Reject(string yaml)
    {
        if (string.IsNullOrEmpty(yaml))
        {
            return;
        }

        var scanner = new Scanner(new StringReader(yaml), skipComments: true);
        var frames = new Stack<Frame>();
        var anchors = new Dictionary<string, Node>(StringComparer.Ordinal);
        string? pendingAnchor = null;

        while (scanner.MoveNext())
        {
            if (scanner.Current is not Token token)
            {
                continue;
            }

            switch (token)
            {
                case Anchor anchor:
                    pendingAnchor = anchor.Value.Value;
                    break;

                case AnchorAlias alias:
                    Attach(frames, NodeForAlias(anchors, alias, pendingAnchor), alias.Start, alias.End, alias.Value.Value);
                    pendingAnchor = null;
                    break;

                case Scalar scalar:
                    var scalarNode = new Node();
                    Register(anchors, ref pendingAnchor, scalarNode);
                    Attach(frames, scalarNode, scalar.Start, scalar.End, null);
                    break;

                case FlowSequenceStart:
                case BlockSequenceStart:
                    var sequence = new Node { Sequence = new List<Link>() };
                    Register(anchors, ref pendingAnchor, sequence);
                    frames.Push(new Frame(sequence));
                    break;

                case FlowMappingStart:
                case BlockMappingStart:
                    var mapping = new Node { Mapping = new List<Entry>() };
                    Register(anchors, ref pendingAnchor, mapping);
                    frames.Push(new Frame(mapping));
                    break;

                case FlowSequenceEnd:
                case FlowMappingEnd:
                case BlockEnd:
                    if (frames.Count == 0)
                    {
                        throw Lost(token);
                    }

                    var closed = frames.Pop();
                    if ((token is FlowMappingEnd && closed.Node.Mapping is null)
                        || (token is FlowSequenceEnd && closed.Node.Sequence is null))
                    {
                        throw Lost(token);
                    }

                    if (closed.Node.Mapping is not null && closed.PendingKey is not null)
                    {
                        Complete(closed, NullLink(token));
                    }

                    Attach(frames, closed.Node, token.Start, token.End, null);
                    break;

                case DocumentStart:
                    if (frames.Count != 0)
                    {
                        throw Lost(token);
                    }

                    anchors.Clear();
                    pendingAnchor = null;
                    break;

                case Key:
                    var keyFrame = Mapping(frames, token);
                    if (keyFrame.Phase == Phase.Value && keyFrame.PendingKey is not null)
                    {
                        Complete(keyFrame, NullLink(token));
                    }

                    keyFrame.Phase = Phase.Key;
                    break;

                case Value:
                    var valueFrame = Mapping(frames, token);
                    if (valueFrame.PendingKey is null)
                    {
                        valueFrame.PendingKey = NullLink(token);
                    }

                    valueFrame.Phase = Phase.Value;
                    break;
            }
        }
    }

    private const string LostMessage = "the YAML document could not be checked for a self-referential mapping key.";

    private static void Attach(Stack<Frame> frames, Node node, Mark start, Mark end, string? aliasName)
    {
        var link = new Link(node, start, end, aliasName);
        if (frames.Count == 0)
        {
            return;
        }

        var parent = frames.Peek();
        if (parent.Node.Sequence is { } sequence)
        {
            sequence.Add(link);
            return;
        }

        if (parent.Phase == Phase.Key)
        {
            parent.PendingKey = link;
            parent.Phase = Phase.None;
            return;
        }

        if (parent.Phase == Phase.Value && parent.PendingKey is { } key)
        {
            RejectCyclicKey(key);
            parent.Node.Mapping!.Add(new Entry(key, link));
            parent.PendingKey = null;
            parent.Phase = Phase.None;
            return;
        }

        // `{a:}` is a single scalar token and an implicit null value.
        if (parent.Phase == Phase.None && parent.Node.Mapping is not null)
        {
            RejectCyclicKey(link);
            parent.Node.Mapping.Add(new Entry(link, new Link(new Node(), start, end, null)));
            return;
        }

        throw new YamlException(start, end, LostMessage);
    }

    private static void Complete(Frame frame, Link value)
    {
        if (frame.PendingKey is not { } key)
        {
            return;
        }

        RejectCyclicKey(key);
        frame.Node.Mapping!.Add(new Entry(key, value));
        frame.PendingKey = null;
        frame.Phase = Phase.None;
    }

    private static Link NullLink(Token token) => new(new Node(), token.Start, token.End, null);

    private static void RejectCyclicKey(Link key)
    {
        var color = new Dictionary<Node, byte>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Walk>();
        color[key.Target] = 1;
        stack.Push(new Walk(key.Target, 0));
        while (stack.Count > 0)
        {
            var walk = stack.Pop();
            var child = ChildAt(walk.Node, walk.Index);
            if (child is null)
            {
                color[walk.Node] = 2;
                continue;
            }

            stack.Push(new Walk(walk.Node, walk.Index + 1));
            if (!color.TryGetValue(child.Value.Target, out var seen))
            {
                color[child.Value.Target] = 1;
                stack.Push(new Walk(child.Value.Target, 0));
            }
            else if (seen == 1)
            {
                throw new YamlException(child.Value.Start, child.Value.End, CycleMessage(child.Value.AliasName));
            }
        }
    }

    private static Link? ChildAt(Node node, int index)
    {
        if (node.Sequence is { } sequence)
        {
            return index < sequence.Count ? sequence[index] : null;
        }

        if (node.Mapping is not { } mapping)
        {
            return null;
        }

        var cursor = 0;
        foreach (var entry in mapping)
        {
            if (cursor++ == index)
            {
                return entry.Key;
            }

            // YamlMappingNode.GetHashCode hashes an unanchored value and only the
            // anchor name of an anchored one. An unresolved alias carries its name.
            if (string.IsNullOrEmpty(entry.Value.Target.Anchor) && cursor++ == index)
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static Node NodeForAlias(Dictionary<string, Node> anchors, AnchorAlias alias, string? pendingAnchor)
    {
        if (!anchors.TryGetValue(alias.Value.Value, out var existing))
        {
            existing = new Node { Anchor = alias.Value.Value };
        }

        if (pendingAnchor is not null)
        {
            anchors[pendingAnchor] = existing;
        }

        return existing;
    }

    private static void Register(Dictionary<string, Node> anchors, ref string? pendingAnchor, Node node)
    {
        if (pendingAnchor is null)
        {
            return;
        }

        node.Anchor = pendingAnchor;
        anchors[pendingAnchor] = node;
        pendingAnchor = null;
    }

    private static Frame Mapping(Stack<Frame> frames, Token token)
    {
        if (frames.Count == 0 || frames.Peek().Node.Mapping is null)
        {
            throw Lost(token);
        }

        return frames.Peek();
    }

    private static YamlException Lost(Token token) => new(token.Start, token.End, LostMessage);

    private static string CycleMessage(string? anchorName)
    {
        if (string.IsNullOrEmpty(anchorName))
        {
            return "a mapping key contains a node that refers to itself. The document is refused before it is loaded.";
        }

        return "a mapping key contains an alias to anchor '" + Describe(anchorName)
            + "', which refers to a node that contains the key. The document is refused before it is loaded.";
    }

    private static string Describe(string name)
    {
        var text = new StringBuilder();
        var count = Math.Min(name.Length, 64);
        for (var i = 0; i < count; i++)
        {
            var character = name[i];
            if (character is >= ' ' and <= '~' and not '\\' and not '\'')
            {
                text.Append(character);
            }
            else
            {
                text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        if (name.Length > 64)
        {
            text.Append("...");
        }

        return text.ToString();
    }

    private sealed class Node
    {
        public string? Anchor { get; set; }

        public List<Link>? Sequence { get; set; }

        public List<Entry>? Mapping { get; set; }
    }

    private sealed class Frame
    {
        public Frame(Node node) => Node = node;

        public Node Node { get; }

        public Link? PendingKey { get; set; }

        public Phase Phase { get; set; }
    }

    private enum Phase
    {
        None,
        Key,
        Value,
    }

    private readonly record struct Entry(Link Key, Link Value);

    private readonly record struct Link(Node Target, Mark Start, Mark End, string? AliasName);

    private readonly record struct Walk(Node Node, int Index);
}
