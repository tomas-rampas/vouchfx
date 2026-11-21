// Scans a YAML event stream for the one alias shape that makes YamlDotNet 16.3.0
// abort the process. Compiled into every assembly that calls YamlStream.Load:
// Authoring picks this file up by directory, and Compilation and Orchestration
// link it, because the guard is internal and those assemblies cannot see
// Authoring internals. A public type would ship on a published contract surface.

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vouchfx.Yaml;

/// <summary>
/// Refuses a YAML alias that <see cref="YamlDotNet.RepresentationModel.YamlStream"/> would hash
/// while the node still contains itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>YamlMappingNode.Load</c> hashes every mapping key, and
/// <c>YamlSequenceNode.GetHashCode</c> walks every child with no cycle check. An alias to a
/// sequence that contains that sequence therefore raises an uncatchable
/// <see cref="StackOverflowException"/> inside <c>Load</c>. The event stream never builds the
/// representation model, so the scan itself cannot overflow.
/// </para>
/// <para>
/// Hash reachability matches the pinned library. A sequence hashes every child. A mapping
/// hashes every key, and hashes a value only when that value has no anchor. A sequence that
/// refers to itself only as a value, and a mapping that refers to itself as a value, still
/// load and are not refused.
/// </para>
/// </remarks>
internal static class YamlAnchorCycleGuard
{
    internal readonly record struct Cycle(string Anchor, long Line, long Column)
    {
        public string Message =>
            $"YAML alias '*{Anchor}' at line {Line}, column {Column} points at a node that contains a sequence which contains itself, and that alias is hashed as a mapping key. Loading it overflows the stack. Remove the self-reference.";
    }

    internal static Cycle? Find(string yamlText)
    {
        try
        {
            return Scan(yamlText);
        }
        catch (YamlException)
        {
            // Ordinary syntax errors still belong to Load, which reports them.
            return null;
        }
    }

    private static Cycle? Scan(string yamlText)
    {
        var stack = new Stack<Frame>();
        var openSequences = new HashSet<AnchorName>();
        var dangerous = new HashSet<AnchorName>();
        var containedBy = new Dictionary<AnchorName, HashSet<AnchorName>>();
        var contains = new Dictionary<AnchorName, HashSet<AnchorName>>();

        var parser = new Parser(new StringReader(yamlText));
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                case DocumentEnd:
                    stack.Clear();
                    openSequences.Clear();
                    dangerous.Clear();
                    containedBy.Clear();
                    contains.Clear();
                    break;

                case SequenceStart start:
                    Start(stack, openSequences, isMapping: false, start.Anchor);
                    break;

                case MappingStart start:
                    Start(stack, openSequences, isMapping: true, start.Anchor);
                    break;

                case SequenceEnd:
                case MappingEnd:
                    End(stack, openSequences, dangerous, containedBy);
                    break;

                case Scalar:
                    FinishChild(stack);
                    break;

                case AnchorAlias alias:
                    var cycle = OnAlias(alias, stack, openSequences, dangerous, containedBy, contains);
                    if (cycle is not null)
                    {
                        return cycle;
                    }

                    FinishChild(stack);
                    break;
            }
        }

        return null;
    }

    private static void Start(Stack<Frame> stack, HashSet<AnchorName> openSequences, bool isMapping, AnchorName anchor)
    {
        var parent = stack.Count == 0 ? null : stack.Peek();
        stack.Push(new Frame
        {
            IsMapping = isMapping,
            Anchor = anchor,
            Hashed = ChildIsHashed(parent, !anchor.IsEmpty),
        });
        if (!isMapping && !anchor.IsEmpty)
        {
            openSequences.Add(anchor);
        }
    }

    private static void End(
        Stack<Frame> stack,
        HashSet<AnchorName> openSequences,
        HashSet<AnchorName> dangerous,
        Dictionary<AnchorName, HashSet<AnchorName>> containedBy)
    {
        if (stack.Count == 0)
        {
            return;
        }

        var frame = stack.Pop();
        if (!frame.IsMapping && !frame.Anchor.IsEmpty)
        {
            openSequences.Remove(frame.Anchor);
        }

        if (frame.Dangerous)
        {
            NoteDangerousChild(stack.Count == 0 ? null : stack.Peek(), !frame.Anchor.IsEmpty, dangerous, containedBy, stack);
        }

        FinishChild(stack);
    }

    private static Cycle? OnAlias(
        AnchorAlias alias,
        Stack<Frame> stack,
        HashSet<AnchorName> openSequences,
        HashSet<AnchorName> dangerous,
        Dictionary<AnchorName, HashSet<AnchorName>> containedBy,
        Dictionary<AnchorName, HashSet<AnchorName>> contains)
    {
        var parent = stack.Count == 0 ? null : stack.Peek();
        var target = alias.Value;
        foreach (var frame in stack)
        {
            if (!frame.IsMapping && !frame.Anchor.IsEmpty)
            {
                AddEdge(frame.Anchor, target, contains, containedBy);
            }
        }

        if (!IsDangerousTarget(target, openSequences, dangerous, contains))
        {
            return null;
        }

        MarkDangerous(target, dangerous, containedBy, stack);
        foreach (var frame in stack)
        {
            if (!frame.IsMapping && !frame.Anchor.IsEmpty)
            {
                MarkDangerous(frame.Anchor, dangerous, containedBy, stack);
            }
        }

        NoteDangerousChild(parent, childHasAnchor: true, dangerous, containedBy, stack);
        if (!ChildIsHashed(parent, childHasAnchor: true))
        {
            return null;
        }

        return new Cycle(target.Value, alias.Start.Line, alias.Start.Column);
    }

    private static bool IsDangerousTarget(
        AnchorName target,
        HashSet<AnchorName> openSequences,
        HashSet<AnchorName> dangerous,
        Dictionary<AnchorName, HashSet<AnchorName>> contains)
    {
        if (target.IsEmpty)
        {
            return false;
        }

        if (dangerous.Contains(target) || openSequences.Contains(target))
        {
            return true;
        }

        var seen = new HashSet<AnchorName>();
        var work = new Stack<AnchorName>();
        work.Push(target);
        while (work.Count > 0)
        {
            var name = work.Pop();
            if (!seen.Add(name))
            {
                continue;
            }

            if (openSequences.Contains(name))
            {
                return true;
            }

            if (contains.TryGetValue(name, out var next))
            {
                foreach (var child in next)
                {
                    work.Push(child);
                }
            }
        }

        return false;
    }

    private static void NoteDangerousChild(
        Frame? parent,
        bool childHasAnchor,
        HashSet<AnchorName> dangerous,
        Dictionary<AnchorName, HashSet<AnchorName>> containedBy,
        Stack<Frame> stack)
    {
        if (parent is null)
        {
            return;
        }

        var hashes = !parent.IsMapping || parent.NextIsKey || !childHasAnchor;
        if (!hashes)
        {
            return;
        }

        parent.Dangerous = true;
        if (!parent.Anchor.IsEmpty)
        {
            MarkDangerous(parent.Anchor, dangerous, containedBy, stack);
        }
    }

    private static void MarkDangerous(
        AnchorName start,
        HashSet<AnchorName> dangerous,
        Dictionary<AnchorName, HashSet<AnchorName>> containedBy,
        Stack<Frame> stack)
    {
        var work = new Stack<AnchorName>();
        work.Push(start);
        while (work.Count > 0)
        {
            var name = work.Pop();
            if (name.IsEmpty || !dangerous.Add(name))
            {
                continue;
            }

            foreach (var frame in stack)
            {
                if (frame.Anchor.Equals(name))
                {
                    frame.Dangerous = true;
                }
            }

            if (containedBy.TryGetValue(name, out var parents))
            {
                foreach (var parent in parents)
                {
                    work.Push(parent);
                }
            }
        }
    }

    private static void AddEdge(
        AnchorName from,
        AnchorName to,
        Dictionary<AnchorName, HashSet<AnchorName>> contains,
        Dictionary<AnchorName, HashSet<AnchorName>> containedBy)
    {
        if (from.IsEmpty || to.IsEmpty)
        {
            return;
        }

        if (!contains.TryGetValue(from, out var children))
        {
            contains[from] = children = new HashSet<AnchorName>();
        }

        if (!children.Add(to))
        {
            return;
        }

        if (!containedBy.TryGetValue(to, out var parents))
        {
            containedBy[to] = parents = new HashSet<AnchorName>();
        }

        parents.Add(from);
    }

    private static bool ChildIsHashed(Frame? parent, bool childHasAnchor)
    {
        if (parent is null)
        {
            return false;
        }

        // TryAdd hashes every mapping key, whether or not anyone hashes the mapping.
        if (parent.IsMapping && parent.NextIsKey)
        {
            return true;
        }

        if (!parent.Hashed)
        {
            return false;
        }

        if (!parent.IsMapping)
        {
            return true;
        }

        // A hashed mapping recurses into a value only when the value has no anchor.
        return !childHasAnchor;
    }

    private static void FinishChild(Stack<Frame> stack)
    {
        if (stack.Count == 0)
        {
            return;
        }

        var parent = stack.Peek();
        if (parent.IsMapping)
        {
            parent.NextIsKey = !parent.NextIsKey;
        }
    }

    private sealed class Frame
    {
        public bool IsMapping { get; init; }

        public AnchorName Anchor { get; init; }

        public bool Hashed { get; init; }

        public bool NextIsKey { get; set; } = true;

        public bool Dangerous { get; set; }
    }
}
