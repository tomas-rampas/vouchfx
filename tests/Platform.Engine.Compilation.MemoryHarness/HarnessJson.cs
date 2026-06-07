// Platform.Engine.Compilation.MemoryHarness — HarnessJson (S01-B-03, §5).
//
// Holds a single cached JsonSerializerOptions instance for the harness executable.
// Caching avoids CA1869 (do not create a new JsonSerializerOptions per serialisation
// call) and the cost of repeated metadata reflection inside System.Text.Json.
using System.Text.Json;

namespace Platform.Engine.Compilation.MemoryHarness;

/// <summary>
/// Provides a single cached <see cref="JsonSerializerOptions"/> instance for use
/// throughout the memory harness executable.
/// </summary>
internal static class HarnessJson
{
    /// <summary>
    /// Compact (non-indented) serialiser options used to emit the single-line JSON
    /// object to stdout.  The instance is created once at startup and reused.
    /// </summary>
    internal static readonly JsonSerializerOptions Options =
        new JsonSerializerOptions { WriteIndented = false };
}
