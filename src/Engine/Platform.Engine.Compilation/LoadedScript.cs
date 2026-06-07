// Platform.Engine.Compilation — LoadedScript (§5).
//
// Encapsulates a single "load once → invoke N times → unload once" lifecycle:
//
//   1. ONE new CollectibleScriptLoadContext is created.
//   2. The emitted image is loaded into it ONCE (LoadFromStream).
//   3. The Roslyn-emitted <Factory> MethodInfo is resolved ONCE and bound to a typed
//      Func<object[], Task<object?>> delegate via MethodInfo.CreateDelegate — so
//      subsequent calls do NOT pay per-call reflection overhead.
//   4. The caller may invoke InvokeAsync any number of times; each call builds a fresh
//      object[] submission array and awaits the already-bound delegate.
//   5. Dispose() calls AssemblyLoadContext.Unload() exactly once and nulls all
//      delegate / assembly references so the GC can reclaim the collectible context.
//
// This is the "load-once-invoke-many" complement to RunIsolatedAsync's
// "new-ALC-per-call" pattern.  Both are deliberate:
//   • RunIsolatedAsync — maximum per-scenario isolation (one ALC per test run).
//   • LoadedScript     — maximum throughput for high-iteration load scenarios and the
//                        S01-B-03 memory harness (one ALC for N invocations).
using System.Reflection;
using System.Runtime.Loader;
using Platform.Engine.Abstractions;

namespace Platform.Engine.Compilation;

/// <summary>
/// A loaded, reusable handle to a compiled Roslyn script that lives inside a single
/// collectible <see cref="AssemblyLoadContext"/> for its entire lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle:</b> obtain via <see cref="RoslynScriptCompiler.Load"/>.  Call
/// <see cref="InvokeAsync"/> any number of times.  Call <see cref="Dispose"/> exactly
/// once when the caller is finished — this triggers
/// <see cref="AssemblyLoadContext.Unload"/> and nulls all internal references so the GC
/// can reclaim the collectible context and its emitted assembly.
/// </para>
/// <para>
/// <b>Thread safety:</b> concurrent calls to <see cref="InvokeAsync"/> are safe — each
/// call constructs its own submission array so there is no shared mutable state in the
/// hot path.  <see cref="Dispose"/> must not be called while any
/// <see cref="InvokeAsync"/> invocation is still in flight; the caller must ensure all
/// outstanding tasks complete before disposing.  Concurrent <see cref="Dispose"/> during
/// an in-flight <see cref="InvokeAsync"/> is undefined behaviour; no internal
/// synchronisation guards against it.
/// </para>
/// <para>
/// <b>Memory model invariant (§5):</b> no field holds a reference to a type from
/// inside the collectible context beyond the delegate itself; the delegate is nulled on
/// <see cref="Dispose"/> so the GC has no remaining root preventing collection.
/// </para>
/// </remarks>
public sealed class LoadedScript : IDisposable
{
    // The collectible ALC that owns the emitted submission assembly.
    private CollectibleScriptLoadContext? _alc;

    // Pre-built delegate bound to <Factory> — avoids per-call MethodInfo.Invoke overhead.
    // Nulled on Dispose so the GC can traverse the ALC's unload graph freely.
    private Func<object?[], Task<object?>>? _factory;

    // -------------------------------------------------------------------------
    // Internal constructor — callers use RoslynScriptCompiler.Load.
    // -------------------------------------------------------------------------

    internal LoadedScript(CollectibleScriptLoadContext alc, Func<object?[], Task<object?>> factory)
    {
        _alc = alc;
        _factory = factory;
    }

    // -------------------------------------------------------------------------
    // Test-support helper (internal — visible via InternalsVisibleTo)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a <see cref="WeakReference"/> to the underlying
    /// <see cref="CollectibleScriptLoadContext"/> so collectibility tests can verify
    /// that the ALC is reclaimed after <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// This method exists solely for the memory-model test suite.  It must be called
    /// <em>before</em> <see cref="Dispose"/> (whilst <c>_alc</c> is still set) and the
    /// returned weak reference must not be stored anywhere that outlives the dispose
    /// call — otherwise it would root the ALC and prevent collection.
    /// </remarks>
    internal WeakReference AlcWeakReferenceForTest()
    {
        var alc = _alc
            ?? throw new ObjectDisposedException(nameof(LoadedScript),
                "AlcWeakReferenceForTest called after Dispose.");
        return new WeakReference(alc);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Invokes the script body against the supplied <paramref name="globals"/> and
    /// returns the script's return value (or <see langword="null"/> when the body has
    /// no explicit return).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate was bound once at load time; this method only constructs the
    /// two-element submission array and awaits the already-bound delegate.  No
    /// reflection overhead is incurred per call.
    /// </para>
    /// <para>
    /// <b>Concurrency contract:</b> concurrent calls to <see cref="InvokeAsync"/> from
    /// multiple threads are safe — each call allocates its own submission array so there
    /// is no shared mutable state in the hot path.  However, <see cref="Dispose"/> MUST
    /// NOT be called whilst any <see cref="InvokeAsync"/> invocation is still in flight.
    /// The caller is responsible for ensuring all outstanding tasks have completed before
    /// disposing.  Concurrent <see cref="Dispose"/> during an in-flight
    /// <see cref="InvokeAsync"/> is undefined behaviour and will corrupt the ALC
    /// lifecycle; no synchronisation is provided inside this class to prevent it.
    /// </para>
    /// </remarks>
    /// <param name="globals">
    /// Per-invocation state.  The script may read and mutate
    /// <see cref="ScriptGlobalVariables.Vars"/>; mutations are visible to the caller
    /// after the returned <see cref="Task{TResult}"/> completes.
    /// </param>
    /// <param name="ct">Propagated to any async operations inside the script body.</param>
    /// <returns>The script's return value, or <see langword="null"/>.</returns>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if <see cref="Dispose"/> has already been called before this method is
    /// entered.  Does not protect against a concurrent <see cref="Dispose"/> that races
    /// this call.
    /// </exception>
    public Task<object?> InvokeAsync(
        ScriptGlobalVariables globals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(globals);

        var factory = _factory
            ?? throw new ObjectDisposedException(nameof(LoadedScript),
                "InvokeAsync called after Dispose.");

        // Roslyn's submission model: submissionArray[0] = globals, submissionArray[1] = null
        // (previous submission result; always null for the first/only submission).
        var submissionArray = new object?[] { globals, null };
        return factory(submissionArray);
    }

    /// <summary>
    /// Unloads the collectible <see cref="AssemblyLoadContext"/> and releases all
    /// internal references so the GC can reclaim the emitted assembly.
    /// </summary>
    /// <remarks>
    /// Safe to call once; subsequent calls are no-ops (idempotent).
    /// Do NOT call while <see cref="InvokeAsync"/> invocations are still in flight.
    /// </remarks>
    public void Dispose()
    {
        // Null the delegate BEFORE calling Unload so no root remains through this object.
        _factory = null;

        var alc = Interlocked.Exchange(ref _alc, null);
        alc?.Unload();
    }
}
