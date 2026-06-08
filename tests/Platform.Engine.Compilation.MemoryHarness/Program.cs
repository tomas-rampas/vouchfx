// Platform.Engine.Compilation.MemoryHarness — entry point (S01-B-03 / S02-B-01, §5).
//
// Parses CLI arguments, drives MemoryProbe.RunAsync or MemoryProbe.RunClosureAsync,
// emits machine-readable JSON to stdout and a human-readable summary to stderr.
//
// Exit codes:
//   0 — Passed == true AND ContextReclaimed == true (heap delta below threshold,
//       all collectible ALCs reclaimed by the GC).
//   1 — Passed == false OR ContextReclaimed == false (potential memory leak).
//
// Usage:
//   dotnet run -c Release --project tests/Platform.Engine.Compilation.MemoryHarness
//   dotnet run -c Release --project tests/Platform.Engine.Compilation.MemoryHarness \
//       -- --mode closure --iterations 5000 --threshold-bytes 2000000
//   dotnet run -c Release --project tests/Platform.Engine.Compilation.MemoryHarness \
//       -- --mode trivial --iterations 5000

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Platform.Engine.Compilation.MemoryHarness;

// Cached options instance — avoids CA1869 (do not create JsonSerializerOptions per call).
var jsonOptions = HarnessJson.Options;

// ── Defaults ──────────────────────────────────────────────────────────────────
int iterations = 5_000;
long thresholdBytes = 2_000_000;
string mode = MemoryProbe.ClosureMode; // Default: closure is the real M1 gate.

// ── Argument parsing ──────────────────────────────────────────────────────────
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--iterations" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[i + 1], out iterations) || iterations <= 0)
        {
            await Console.Error.WriteLineAsync(
                $"[MemoryHarness] Invalid --iterations value: '{args[i + 1]}'. Must be a positive integer.")
                .ConfigureAwait(false);
            return 1;
        }

        i++;
    }
    else if (args[i] == "--threshold-bytes" && i + 1 < args.Length)
    {
        if (!long.TryParse(args[i + 1], out thresholdBytes) || thresholdBytes <= 0)
        {
            await Console.Error.WriteLineAsync(
                $"[MemoryHarness] Invalid --threshold-bytes value: '{args[i + 1]}'. Must be a positive integer.")
                .ConfigureAwait(false);
            return 1;
        }

        i++;
    }
    else if (args[i] == "--mode" && i + 1 < args.Length)
    {
        var raw = args[i + 1];
        if (raw != "closure" && raw != "trivial")
        {
            await Console.Error.WriteLineAsync(
                $"[MemoryHarness] Invalid --mode value: '{raw}'. Must be 'closure' or 'trivial'.")
                .ConfigureAwait(false);
            return 1;
        }

        mode = raw;
        i++;
    }
}

await Console.Error.WriteLineAsync(
    $"[MemoryHarness] Starting: mode={mode}, iterations={iterations:N0}, threshold={thresholdBytes:N0} bytes.")
    .ConfigureAwait(false);

// ── Run the measurement ───────────────────────────────────────────────────────
var result = mode == MemoryProbe.ClosureMode
    ? await MemoryProbe.RunClosureAsync(iterations, thresholdBytes).ConfigureAwait(false)
    : await MemoryProbe.RunAsync(iterations, thresholdBytes).ConfigureAwait(false);

// ── Stdout: machine-readable JSON (pure — no other writes to stdout) ──────────
var json = JsonSerializer.Serialize(result, jsonOptions);
Console.WriteLine(json);

// ── Stderr: human-readable summary ───────────────────────────────────────────
await Console.Error.WriteLineAsync(
    $"[MemoryHarness] Mode={result.Mode}, " +
    $"NetDelta={result.NetDeltaBytes:+#;-#;0} bytes " +
    $"({result.NetDeltaBytes / 1024.0:F1} KB), " +
    $"Threshold={result.ThresholdBytes:N0} bytes, " +
    $"Passed={result.Passed}, " +
    $"CollectibleBefore={result.CollectibleBefore}, " +
    $"CollectibleAfter={result.CollectibleAfter}, " +
    $"ContextReclaimed={result.ContextReclaimed}.")
    .ConfigureAwait(false);

if (result.SingletonsReset.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"[MemoryHarness] SingletonsReset ({result.SingletonsReset.Count}):")
        .ConfigureAwait(false);

    foreach (var entry in result.SingletonsReset)
    {
        await Console.Error.WriteLineAsync($"  • {entry}").ConfigureAwait(false);
    }
}

// ── Exit code ─────────────────────────────────────────────────────────────────
return result.Passed && result.ContextReclaimed ? 0 : 1;
