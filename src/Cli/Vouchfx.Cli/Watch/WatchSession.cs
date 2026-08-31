// Vouchfx.Cli — WatchSession (S08-C-01, watch mode).
//
// The testable core of `vouchfx run <file> --watch`.  It owns ONE topology across re-runs and,
// on each save, decides whether to RE-USE the kept topology (every input the topology was built
// from is unchanged — the expensive container build is skipped) or REBUILD it.  This is the
// no-rebuild re-run seam factored out of the thin FileSystemWatcher shell (WatchCommand), so the
// reuse/rebuild decision is unit-testable WITHOUT a real watcher and WITHOUT a real container —
// the build / run / dispose / compile seams are injected.
//
// Reuse decision (the acceptance):
//   • first save (no kept topology)           → build a topology, run.
//   • later save, fingerprint UNCHANGED       → run against the KEPT topology (no rebuild).
//   • later save, fingerprint CHANGED         → dispose the old topology, build a new one, run.
//   • save refused by a pre-topology gate     → the seam has rendered it; KEEP WATCHING and do
//                                               NOT build or reuse a topology for it (#370).
//   • parse failure on a save                 → report it and KEEP WATCHING (the kept topology,
//                                               if any, is left intact — a typo mid-edit must
//                                               not stop the loop or tear down infrastructure).
//
// Generic over the topology handle TTopology so the production wiring passes the engine's
// IKeptTopology seam while tests pass a counting fake — the session never names a concrete engine
// type.

using System.Threading;
using System.Threading.Tasks;

namespace Vouchfx.Cli.Watch;

/// <summary>
/// The testable session behind <c>vouchfx run &lt;file&gt; --watch</c>: holds the current
/// topology (and the fingerprint it was built from) across re-runs and decides, per save, whether
/// to re-use or rebuild it (S08-C-01).
/// </summary>
/// <typeparam name="TTopology">
/// The topology handle type.  The production wiring uses the engine's
/// <c>IKeptTopology</c> seam (#364); tests substitute a lightweight fake so the reuse/rebuild
/// decision — and, since the seam exists, everything the run seam does with a topology — is
/// exercised without a container.
/// </typeparam>
/// <remarks>
/// <para>
/// <strong>Reuse is the whole point:</strong> building the Aspire topology (image pull,
/// container start, health gate) is the expensive part of a run.  When successive saves leave
/// every input the topology was built from unchanged — the common case during local iteration,
/// where the author is editing a step's body rather than what it targets — the kept topology is
/// re-used and only the cheap scenario re-runs (the run seam is expected to reset mutable
/// dependency state itself).
/// </para>
/// <para>
/// <strong>Single-threaded by contract:</strong> <see cref="OnChangeAsync"/> is invoked
/// serially by the debounced watcher shell (one save processed at a time); the session keeps no
/// internal lock and assumes no overlapping calls.
/// </para>
/// </remarks>
internal sealed class WatchSession<TTopology> : IAsyncDisposable
    where TTopology : class
{
    /// <summary>
    /// Parses, validates and plans the watched file's current contents into a
    /// <see cref="WatchCompileResult"/> (success → topology fingerprint + opaque planned payload;
    /// refusal → an already-rendered non-success with no message; failure → an error message).
    /// Synchronous: planning is CPU-bound and fast relative to the topology build.
    /// </summary>
    private readonly Func<string, WatchCompileResult> _compile;

    /// <summary>
    /// Builds a fresh topology for the opaque planned payload, returning the topology handle.  The
    /// payload is supplied because the real build derives the topology request from the plan the
    /// compile seam already produced.
    /// </summary>
    private readonly Func<object, CancellationToken, Task<TTopology>> _buildTopologyAsync;

    /// <summary>
    /// Runs the compiled scenario against an EXISTING topology.  This is the no-rebuild re-run
    /// seam.  The <see cref="bool"/> argument is the <c>resetAndReseed</c> signal: <see
    /// langword="false"/> on the FIRST run against a just-built+seeded topology (no pre-reset,
    /// no re-seed — it already carries the fresh seed and no prior writes); <see langword="true"/>
    /// on a REUSE run (reset away the previous run's writes, then re-apply the seed to restore
    /// the freshly-built baseline).  This is what stops a seeded scenario's first watch run from
    /// diverging from a plain <c>vouchfx run</c> (S08-T10, B2).
    /// </summary>
    private readonly Func<TTopology, object, bool, CancellationToken, Task> _runAgainstTopologyAsync;

    /// <summary>Disposes a topology handle (tears down its containers).</summary>
    private readonly Func<TTopology, Task> _disposeTopologyAsync;

    /// <summary>Reports a single human-readable line (status / error) to the user.</summary>
    private readonly Action<string> _report;

    // The kept topology and the topology fingerprint it was built from.  Both null until the first
    // successful build; cleared together when a topology is disposed.
    private TTopology? _current;
    private string? _currentTopologyFingerprint;
    private bool _disposed;

    /// <summary>
    /// Initialises a new <see cref="WatchSession{TTopology}"/> with its injected seams.
    /// </summary>
    /// <param name="compile">Re-read/validate/plan the file contents (see field docs).</param>
    /// <param name="buildTopologyAsync">Build a fresh topology (see field docs).</param>
    /// <param name="runAgainstTopologyAsync">Run a compiled scenario against a kept topology.</param>
    /// <param name="disposeTopologyAsync">Dispose a topology handle.</param>
    /// <param name="report">Report a status / error line to the user.</param>
    public WatchSession(
        Func<string, WatchCompileResult> compile,
        Func<object, CancellationToken, Task<TTopology>> buildTopologyAsync,
        Func<TTopology, object, bool, CancellationToken, Task> runAgainstTopologyAsync,
        Func<TTopology, Task> disposeTopologyAsync,
        Action<string> report)
    {
        _compile = compile ?? throw new ArgumentNullException(nameof(compile));
        _buildTopologyAsync = buildTopologyAsync
            ?? throw new ArgumentNullException(nameof(buildTopologyAsync));
        _runAgainstTopologyAsync = runAgainstTopologyAsync
            ?? throw new ArgumentNullException(nameof(runAgainstTopologyAsync));
        _disposeTopologyAsync = disposeTopologyAsync
            ?? throw new ArgumentNullException(nameof(disposeTopologyAsync));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>
    /// Processes one save of the watched file: re-compile its contents, then re-use or rebuild
    /// the topology and run the scenario (S08-C-01).  Invoked once for the initial run and again
    /// for every subsequent change.
    /// </summary>
    /// <param name="fileContent">The current contents of the watched file.</param>
    /// <param name="cancellationToken">Cancels the build / run (e.g. Ctrl-C).</param>
    /// <remarks>
    /// <para>
    /// A non-success is swallowed: the loop keeps watching and the kept topology (if any) is left
    /// running, so the very next valid save re-runs against it without paying the rebuild cost.
    /// This is the "report and keep watching" contract. A parse failure is REPORTED here; a
    /// pre-topology refusal was already rendered by the compile seam and carries no message, so
    /// nothing is printed twice (#370).
    /// </para>
    /// <para>
    /// On a successful plan: when the topology fingerprint matches the kept topology's, the
    /// scenario runs against the existing topology (NO rebuild); otherwise the old topology is
    /// disposed and a new one is built before the run.  The first save always builds (there is
    /// no kept topology yet).
    /// </para>
    /// </remarks>
    public async Task OnChangeAsync(string fileContent, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(fileContent);

        WatchCompileResult compileResult = _compile(fileContent);
        if (!compileResult.IsSuccess)
        {
            // Authoring error mid-edit: report and KEEP WATCHING — do not touch the kept
            // topology, so the next valid save re-runs against it with no rebuild.
            //
            // GUARDED, because a REFUSAL carries no message (#370): the compile seam has already
            // rendered the refusal's diagnostic and its event pair, and reporting a second time
            // would print the same fault twice — once inside the rendered report and once as a bare
            // line under it. A parse failure still carries a message and still reaches the sink.
            // The run path makes the same split at its protocol-conflict seam
            // (`alreadyPrintedMessage`), for the same reason.
            if (compileResult.Error is { } error)
            {
                _report(error);
            }

            return;
        }

        var newTopologyFingerprint = compileResult.TopologyFingerprint!;
        var compiled = compileResult.Compiled!;

        // Decide reuse vs rebuild on the topology fingerprint ALONE: a steps-only edit that changes
        // no TARGET NAME keeps the fingerprint, so the kept topology is re-used and only the cheap
        // scenario re-runs.
        var canReuse = _current is not null
            && string.Equals(
                _currentTopologyFingerprint, newTopologyFingerprint, StringComparison.Ordinal);

        if (!canReuse)
        {
            // Environment changed (or first run): dispose any old topology, then build fresh.
            await DisposeCurrentTopologyAsync().ConfigureAwait(false);

            _current = await _buildTopologyAsync(compiled, cancellationToken).ConfigureAwait(false);
            _currentTopologyFingerprint = newTopologyFingerprint;
        }

        // Run the (re-)compiled scenario against the kept-or-new topology.  `canReuse` IS the
        // reset+reseed signal (S08-T10, B2): on the BUILD path (!canReuse) the just-built
        // topology already carries the fresh seed and no prior writes, so the run seam must NOT
        // reset/reseed (a reset would truncate the seed before run #1 — and Respawn throws on a
        // schema-via-script.csharp DB with no user tables yet).  On the REUSE path (canReuse)
        // the kept topology holds the previous run's writes, so the run seam resets them and
        // re-applies the seed, restoring the freshly-built baseline before this run.
        await _runAgainstTopologyAsync(_current!, compiled, canReuse, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the kept topology, tearing down its containers.  Idempotent — safe to call
    /// multiple times and after a partially-failed session.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeCurrentTopologyAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the current topology (if any) and clears the kept-topology slot together with
    /// its topology fingerprint.  A no-op when no topology is held.
    /// </summary>
    private async Task DisposeCurrentTopologyAsync()
    {
        if (_current is null)
        {
            return;
        }

        var topology = _current;
        _current = null;
        _currentTopologyFingerprint = null;
        await _disposeTopologyAsync(topology).ConfigureAwait(false);
    }
}
