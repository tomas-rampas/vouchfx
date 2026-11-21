// Refuses a YAML document whose collection nesting would overflow the process stack
// inside YamlDotNet before YamlStream.Load or the schema deserialiser runs.
//
// Both of those parsers walk nested collections by recursion. A flow or block
// document of a few tens of thousands of levels — still under the 1 MiB suite cap —
// overflows the stack. That exception cannot be caught, so validate, run and Compose
// configuration loading die instead of reporting a diagnostic. The schema bridge's
// serialiser already refuses a document around 50 levels, which is far below the
// crash, but that check runs only after a successful deserialise and not on every
// Load. This scan is the check that runs first.
//
// The scanner is iterative and this method returns as soon as the depth passes the
// limit, so the cost follows the limit rather than the rest of the file. A quoted
// run of brackets is one scalar and does not add depth.

using System.Globalization;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;

namespace Vouchfx.Engine.YamlSafety;

internal static class YamlNestingLimit
{
    /// <summary>
    /// Deepest collection nesting accepted before a document is refused.
    /// </summary>
    /// <remarks>
    /// Schema conversion already refuses a document at YamlDotNet's serialiser
    /// recursion ceiling of 50, and a real suite sits well under that. 64 leaves
    /// margin for the levels above a nested value and stays far below the nesting
    /// that overflows the parser stack.
    /// </remarks>
    internal const int MaxDepth = 64;

    internal static void Reject(string? yaml)
    {
        if (string.IsNullOrEmpty(yaml))
        {
            return;
        }

        var scanner = new Scanner(new StringReader(yaml), skipComments: true);
        var depth = 0;
        while (scanner.MoveNext())
        {
            if (scanner.Current is not Token token)
            {
                continue;
            }

            switch (token)
            {
                case FlowMappingStart or FlowSequenceStart or BlockMappingStart or BlockSequenceStart:
                    depth++;
                    if (depth > MaxDepth)
                    {
                        throw new YamlException(
                            token.Start,
                            token.End,
                            "the document nests more than "
                            + MaxDepth.ToString(CultureInfo.InvariantCulture)
                            + " collections (reached at line "
                            + token.Start.Line.ToString(CultureInfo.InvariantCulture)
                            + ", column "
                            + token.Start.Column.ToString(CultureInfo.InvariantCulture)
                            + "). It is refused before it is loaded.");
                    }

                    break;

                case FlowMappingEnd or FlowSequenceEnd or BlockEnd:
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;

                case DocumentStart or DocumentEnd:
                    depth = 0;
                    break;
            }
        }
    }
}
