// Vouchfx.Engine.Orchestration — TempDirectoryLedger (#438 follow-up).
//
// THE RACE THIS CLOSES
// ---------------------
// An azureservicebus dependency stages its Config.json directory from an Aspire
// OnBeforeResourceStarted hook, and teardown removes what was staged. Recording each path in a
// list, and snapshotting that list under a lock before deleting, makes the snapshot atomic, but
// a lock around a snapshot cannot stop a hook from running AFTER the snapshot was taken. In the
// pinned Aspire 13.4.2 source, `DcpExecutor.RunApplicationAsync` awaits container creation with
// `Task.WhenAll(createExecutables, createContainers).WaitAsync(ct)`. A cancelled start can
// therefore throw out of `StartAsync`, into the failure path that cleans up, WHILE the
// container-creation task is still running in the background. That task can still publish
// `BeforeResourceStartedEvent` and run the hook after the snapshot has been taken and deleted
// from, which creates a directory the snapshot never saw and that nothing will ever delete.
//
// THE FIX
// -------
// A plain list conflates two questions that need to be answered together: "what has been created
// so far" and "may something still be created". Answering them separately — snapshot under a lock,
// close some OTHER way — is exactly the gap above. This type answers both under the SAME lock
// acquisition: closing IS taking the snapshot, and once closed, every later attempt to stage a new
// directory is refused before it creates anything. There is no window between "teardown decided
// what to delete" and "a hook is still allowed to create something else" for a late hook to land
// in.
//
// WHY REFUSING IS SAFE, NOT MERELY CONVENIENT
// --------------------------------------------
// Read in the pinned Aspire 13.4.2 source (DcpExecutor): the start-event publish that
// runs a subscribed OnBeforeResourceStarted hook is wrapped in a try/catch. That catch logs
// "Failed to create resource {ResourceName}" and publishes an OnResourceFailedToStartContext, and
// the replica loop that actually creates the container runs AFTER the publish, not before. So a
// hook that throws makes Aspire mark exactly that one resource FailedToStart, with no container
// ever created for it — it does not corrupt sibling resources, and it does not escape as an
// unhandled exception from Aspire's own event dispatch. Refusing a late Stage() is therefore a
// clean, already-supported failure shape, not a new way for the engine to crash.
//
// SCOPE
// -----
// One ledger per MappedTopology, and a MappedTopology serves one start: SuiteTopology maps once
// per start, and StubTopology does not use EnvironmentMapper at all. EnvironmentMapper's
// Configure registers the ledger in the builder's services, and HeadlessTopology resolves it
// from there on both of its cleanup paths, so a caller that composes EnvironmentMapper.Map with
// the public HeadlessTopology.StartAsync directly gets the same cleanup. Such a caller that starts
// a second topology from the same MappedTopology finds the ledger closed by the first one's
// teardown, and that start's azureservicebus emulator fails to start rather than leaking a
// directory. Map again for a second start.

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// The engine-owned record of host-filesystem directories a topology's start hooks create as
/// bind-mount sources (#438) — today, <c>EnvironmentMapper</c>'s <c>azureservicebus</c>
/// <c>Config.json</c> directory — and, at the same time, the single authority deciding whether one
/// more may still be created.
/// </summary>
/// <remarks>
/// <para>
/// See this file's header comment for the race this type closes and why the fix is safe. In one
/// sentence: <see cref="Stage"/> and <see cref="Close"/> share one lock, so "record a directory"
/// and "decide nothing more may be recorded" can never interleave — one fully precedes the other
/// for any given call, never partially overlaps it.
/// </para>
/// <para>
/// <b>Invariant.</b> Every directory a call to <see cref="Stage"/> actually creates on disk is
/// either present in the array a later <see cref="Close"/> returns, or <see cref="Stage"/> threw
/// before creating anything at all — a closed ledger (<see cref="InvalidOperationException"/>), or
/// a failing <see cref="System.IO.Directory.CreateDirectory(string)"/> call. There is no third
/// outcome in which a directory exists on disk but appears in no snapshot any cleanup path ever
/// took.
/// </para>
/// <para>
/// Not <see langword="static"/>, not shared: the engine constructs one instance per mapped
/// topology (<c>EnvironmentMapper.Map</c>) and threads it through
/// <c>MappedTopology.AsbTempDirectoriesCreated</c> to both the start hook that stages into it and
/// the one or two cleanup calls that close it, so two topologies running at once never share one
/// ledger's lock or one ledger's closed state.
/// </para>
/// </remarks>
internal sealed class TempDirectoryLedger
{
    private readonly object _gate = new();
    private readonly List<string> _directories = new();
    private bool _closed;

    /// <summary>
    /// Creates <paramref name="directory"/>, records it, and then runs
    /// <paramref name="populate"/> against it — the full sequence inside one lock acquisition.
    /// </summary>
    /// <param name="directory">
    /// The directory to create, via <see cref="System.IO.Directory.CreateDirectory(string)"/>.
    /// Creating an already-existing directory is a no-op (the BCL method's own documented
    /// behaviour), which is what lets a test stage a path it separately asserts about without a
    /// preceding existence check of its own.
    /// </param>
    /// <param name="populate">
    /// Invoked with <paramref name="directory"/> once it has been created and recorded — e.g. to
    /// write the file a bind-mounted container will read. Runs INSIDE the lock this method holds,
    /// so a concurrent <see cref="Close"/> cannot return while a hook is still half-way through
    /// writing; without that, a recursive delete triggered by an already-returned <see cref="Close"/>
    /// could race this write and leave a non-empty directory behind after "cleanup".
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The ledger is already closed. Thrown before <see cref="System.IO.Directory.CreateDirectory(string)"/>
    /// runs, so this call creates nothing on disk. The message is fixed and deliberately does not
    /// name <paramref name="directory"/> — this repository never prints a host path into an
    /// exception or assertion message. See this file's header comment for why Aspire absorbs this
    /// throw as an ordinary "resource failed to start", never as an engine crash.
    /// </exception>
    /// <remarks>
    /// The directory is recorded (added to the ledger) BEFORE <paramref name="populate"/> runs,
    /// deliberately: a <paramref name="populate"/> that itself throws (e.g. a failed write) still
    /// leaves the directory in the next <see cref="Close"/>'s snapshot, so cleanup still removes a
    /// half-populated directory instead of orphaning it.
    /// </remarks>
    internal void Stage(string directory, Action<string> populate)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(populate);

        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException(
                    "This topology's teardown has already removed its temp directories, so a "
                    + "start hook cannot stage another one.");
            }

            Directory.CreateDirectory(directory);
            _directories.Add(directory);
            populate(directory);
        }
    }

    /// <summary>
    /// Permanently closes the ledger and returns a snapshot of every directory
    /// <see cref="Stage"/> had recorded up to this call — both, atomically, under the one lock
    /// acquisition <see cref="Stage"/> itself uses. Idempotent: a second (or later) call returns
    /// the same snapshot again and never throws.
    /// </summary>
    /// <returns>
    /// A fresh array — safe for the caller to enumerate, and to delete directories from, with no
    /// further locking — listing every directory a <see cref="Stage"/> call recorded strictly
    /// before this <see cref="Close"/>, in staging order.
    /// </returns>
    /// <remarks>
    /// This is what turns "no hook can create a directory after teardown has taken its snapshot"
    /// into a guarantee rather than an assumption about Aspire's own event ordering — the
    /// overclaim this type replaces (#438 review). Because every <see cref="Stage"/> call and this
    /// call contend for the same lock, there is no interleaving in which a hook observes the
    /// ledger open, this snapshot is taken, and the hook's directory then lands outside it: that
    /// hook's <see cref="Stage"/> call either finishes entirely before this <see cref="Close"/>
    /// acquires the lock (its directory IS in the returned snapshot) or starts entirely after this
    /// <see cref="Close"/> has already set the closed flag and released the lock (it throws
    /// <see cref="InvalidOperationException"/> and creates nothing).
    /// </remarks>
    internal IReadOnlyList<string> Close()
    {
        lock (_gate)
        {
            _closed = true;
            return _directories.ToArray();
        }
    }

    /// <summary>
    /// Returns a snapshot of every directory <see cref="Stage"/> has recorded so far, WITHOUT
    /// closing the ledger — unlike <see cref="Close"/>, a later <see cref="Stage"/> call remains
    /// legal after this returns.
    /// </summary>
    /// <returns>
    /// A fresh array with the same safety properties as <see cref="Close"/>'s: independent of the
    /// ledger's internal list, so the caller's use of it needs no further locking.
    /// </returns>
    internal IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _directories.ToArray();
        }
    }
}
