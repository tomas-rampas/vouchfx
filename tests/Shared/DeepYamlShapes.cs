using System.Text;

namespace Vouchfx.Tests;

// Linked into the test assemblies that exercise the YAML nesting limit. Each shape is built
// without recursion, so building a 200,000-level input never overflows the stack itself.
internal static class DeepYamlShapes
{
    /// <summary>The nesting limit under test: the 65th open collection is refused.</summary>
    internal const int Limit = 64;

    /// <summary>One MiB of text: the size the suite discovery limit allows.</summary>
    internal const int OneMiB = 1_048_576;

    /// <summary>Four MiB of text: the size the Compose loads allow.</summary>
    internal const int FourMiB = 4 * 1_048_576;

    /// <summary><paramref name="levels"/> flow sequences, each closed, on one line.</summary>
    internal static string ClosedFlowSequences(int levels) =>
        new string('[', levels) + new string(']', levels);

    /// <summary><paramref name="levels"/> opening brackets and nothing else.</summary>
    internal static string UnterminatedFlowSequences(int levels) => new('[', levels);

    /// <summary>One line of block sequence entries (<c>- - - x</c>), <paramref name="levels"/> deep.</summary>
    internal static string OneLineBlockSequences(int levels)
    {
        var text = new StringBuilder(levels * 2 + 1);
        for (var index = 0; index < levels; index++)
        {
            text.Append("- ");
        }

        return text.Append('x').ToString();
    }

    /// <summary>
    /// <paramref name="levels"/> nested block mappings, one key per line, each indented one space
    /// deeper than its parent; the innermost holds a scalar.
    /// </summary>
    internal static string BlockMappings(int levels)
    {
        var text = new StringBuilder();
        for (var index = 0; index < levels; index++)
        {
            text.Append(' ', index).Append("k:");
            text.Append(index == levels - 1 ? " v" : string.Empty).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The large shapes the timing tests refuse: unterminated flow, closed flow and one-line block
    /// input. Only the flow shapes prove the scan stops early (a full parse of deep flow input is
    /// quadratic); a full parse of the block shape is linear, so its rows guard against a crash,
    /// not against cost.
    /// </summary>
    internal static string Large(string shape, int characters) => shape switch
    {
        "flow" => UnterminatedFlowSequences(characters),
        "closed-flow" => ClosedFlowSequences(characters / 2),
        "block" => OneLineBlockSequences((characters - 1) / 2),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };
}
