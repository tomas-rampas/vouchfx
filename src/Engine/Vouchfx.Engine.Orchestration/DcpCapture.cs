// The flush half of the #420 flight recorder: where a capture goes when a topology fails to
// come up, how long captures are kept, and how the evidence reaches the Environment-error
// detail (section 12.1) without changing anything the classifier or the event wire sees.
//
// TWO DESTINATIONS, AND THE REASON THERE ARE TWO.
// -----------------------------------------------
//   1. A per-user capture FILE holds the whole buffer. It is the only place the Debug-level
//      DCP traffic -- which port was tried, what came back -- can live: it is far too large
//      for an event field, and #420's own two failed capture attempts established that it
//      does not reach `dotnet test`'s default output stream either.
//   2. A bounded TAIL plus the capture file's NAME reach the failure itself, and from there
//      the Environment-error detail. This is not redundancy. On a CI runner the filesystem is
//      discarded when the job ends, so the file may never be read by anyone; the tail in the
//      detail is what survives in the job log.
//
// HOW THE EVIDENCE TRAVELS, and why it is not an exception wrapper.
// -----------------------------------------------------------------
// HeadlessTopology.StartAsync rethrows the original exception; SuiteTopology catches it and
// calls OrchestrationErrorClassifier.Classify, whose every heuristic reads Exception.Message.
// Wrapping the exception to carry the capture would therefore change the message the
// classifier classifies on -- a diagnostic that alters the diagnosis. Annotating
// Exception.Data changes no type, no message and no stack trace, survives the rethrow on the
// same instance the classifier later sees, and is invisible to every consumer that does not
// look for the key.

using System.Globalization;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Which root a capture was written under, and therefore which token — if any — can name it in
/// an Environment-error detail without disclosing the resolved path.
/// </summary>
internal enum DcpCaptureRoot
{
    /// <summary>The per-user local application data root: the production default.</summary>
    PerUser,

    /// <summary>The directory named by <see cref="DcpCapture.DirectoryOverrideVariable"/>.</summary>
    EnvironmentOverride,

    /// <summary>
    /// A directory passed directly to the flush by an in-process caller, with no environment
    /// variable behind it. Only the drills reach this: no production call site supplies one.
    /// </summary>
    HostSupplied,
}

/// <summary>
/// Writes, prunes and annotates the diagnostic captures produced by
/// <see cref="DcpFlightRecorder"/> when a topology fails to come up (issue #420).
/// </summary>
/// <remarks>
/// <para>
/// Every member is either pure or takes its filesystem operations as delegates, so the whole
/// flush path -- file naming, retention, the annotation, the detail annex -- is exercisable
/// without Docker, without an Aspire host, and without touching the real capture directory.
/// </para>
/// <para>
/// <strong>What a capture can contain, measured rather than assumed.</strong> The buffer holds
/// whatever Aspire logged while the topology was coming up, which on the DCP path includes
/// container specifications and therefore container environment variables. An earlier version
/// of this remark warned that those could be resolved <c>${secret:}</c> values. That is FALSE
/// on this engine and the over-warning was harmful, because a capture nobody dares attach to
/// #420 is a capture that never explains anything:
/// <c>EnvironmentMapper</c> REFUSES the <c>${secret:</c> sigil outright, case-insensitively, in
/// both <c>services[].env</c> and <c>dependencies[].env</c>, so no resolved secret can reach a
/// container specification for this layer to log. What a capture CAN hold is:
/// </para>
/// <list type="bullet">
///   <item>Aspire's per-run GENERATED passwords for managed dependencies -- throwaway, valid
///   only for that run's container, and destroyed with it.</item>
///   <item>The fixed local test credentials a suite declares, which in this repository's own
///   examples are already public.</item>
///   <item>Any host environment value the author deliberately routed in with
///   <c>${env:NAME}</c>.</item>
///   <item>Absolute host paths from this machine.</item>
/// </list>
/// <para>
/// Every one of those is already visible to <c>docker inspect</c> for the same containers on
/// the same machine, so the file adds no exposure an operator did not already have locally --
/// which is why it goes under the per-user local application data root (owner-only on Unix),
/// is written only when a topology FAILS to come up, and is never uploaded anywhere by the
/// engine. Attaching one to an issue publishes it, so it is worth a skim first. The bounded
/// TAIL is a different question and is answered at <see cref="BuildSummary"/>.
/// </para>
/// </remarks>
internal static class DcpCapture
{
    /// <summary>The directory, under the per-user local application data root, that holds captures.</summary>
    /// <remarks>
    /// The same root as the drill-host sweep record, for the same reason: a capture names
    /// absolute paths and host detail from this machine, and on a shared host the temp
    /// directory is world-readable and world-writable, where a filename there can be
    /// pre-created by another user. It is also outside the repository, whose <c>.gitignore</c>
    /// would hide a stray artefact rather than flag it.
    /// </remarks>
    internal const string DirectoryName = "vouchfx";

    /// <summary>
    /// Environment variable naming an explicit directory for captures, in place of the per-user
    /// root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This exists for CI, not for the drills that also use it.</strong> The per-user
    /// root is the right default on a workstation and the wrong one on a build agent: a runner's
    /// filesystem is discarded when the job ends, so a capture written there is destroyed
    /// unread — and CI is precisely where #420 is hardest to reproduce and most valuable to
    /// have captured. Pointing this at the workspace lets the job's existing artefact upload
    /// carry the capture out. It is the natural companion to
    /// <see cref="DcpFlightRecorder.OptOutVariable"/>: one switch turns the recorder off, the
    /// other says where its output goes.
    /// </para>
    /// <para>
    /// The path is honoured EXACTLY — no <c>vouchfx</c> subdirectory is appended, because an
    /// operator who named a directory meant that directory. It must be ABSOLUTE: a relative
    /// value is refused rather than resolved against the current directory, for the same reason
    /// the per-user root is (see <see cref="ResolveDirectory(string?, string?)"/>), and the
    /// refusal is reported in the failure's annex rather than silently swallowed.
    /// </para>
    /// <para>
    /// It also carries no owner-only guarantee beyond what the named directory already has. The
    /// capture FILE is still mode-restricted on Unix, but the DIRECTORY is narrowed only when
    /// this feature created it - see <see cref="CreateDirectoryOwnerOnly"/>, which deliberately
    /// leaves a pre-existing directory's permissions alone rather than silently changing
    /// something outside its ownership. The per-user default is what protects the operator who
    /// chooses nothing.
    /// </para>
    /// </remarks>
    internal const string DirectoryOverrideVariable = "VOUCHFX_DCP_CAPTURE_DIR";

    /// <summary>
    /// The non-Windows per-user root, written as a shell expression that resolves whether or not
    /// <c>XDG_DATA_HOME</c> is set. See <see cref="DescribeLocation"/> for why the bare variable
    /// was not good enough.
    /// </summary>
    internal const string UnixRootToken = "${XDG_DATA_HOME:-$HOME/.local/share}";

    /// <summary>Capture file-name prefix. Also the glob used by retention.</summary>
    internal const string FileNamePrefix = "dcp-capture-";

    /// <summary>Capture file-name suffix.</summary>
    internal const string FileNameSuffix = ".log";

    /// <summary>How many capture files survive a flush, newest first.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The arithmetic, because the previous value silently destroyed evidence.</strong>
    /// #420's second occurrence was EIGHT consecutive topology-start failures in one session
    /// (three topologies, including an unmodified control). Retention has to cover a whole
    /// reproduction window or it deletes the very captures the window produced -- at five, three
    /// of those eight would already have been pruned before anyone looked. Twelve covers that
    /// measured window with room for the re-runs an operator makes while reading this, and still
    /// bounds an unattended host: twelve captures of a 128 Ki-character buffer is a few
    /// megabytes at the absolute worst, and far less in practice.
    /// </para>
    /// </remarks>
    internal const int RetainedFiles = 12;

    /// <summary>The <see cref="Exception.Data"/> key the capture summary travels under.</summary>
    internal const string DataKey = "vouchfx.dcp-capture";

    /// <summary>Most tail lines carried into an Environment-error detail.</summary>
    internal const int TailEntryLimit = 12;

    /// <summary>Total character budget for those tail lines.</summary>
    /// <remarks>
    /// The budget, not the line count, is what usually binds: at
    /// <see cref="DcpFlightRecorder.TailLineChars"/> per line this admits three or four
    /// full-width warnings, which is the shape #420 presents (one <c>Unable to allocate</c>
    /// warning per endpoint, then the throw). The count is the second bound, for a fault that
    /// emits many short warnings instead.
    /// </remarks>
    internal const int TailCharLimit = 384;

    /// <summary>
    /// Total character bound on everything this type appends to an Environment-error detail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The detail's own <c>MaxDetailLength</c> bound exists to keep an unbounded exception
    /// message -- a multi-kilobyte stack trace -- out of a JSON Lines record. This annex is not
    /// that: every part of it is engine-authored and individually bounded (a fixed note, one
    /// file NAME, a tail already capped by <see cref="TailCharLimit"/>). It is bounded again
    /// here so the composition is bounded by construction rather than by adding up three other
    /// constants and hoping.
    /// </para>
    /// <para>
    /// Appending AFTER the message is truncated, rather than fitting inside the existing bound,
    /// is the deliberate part: truncating the message to make room would spend the evidence to
    /// pay for the pointer to the evidence.
    /// </para>
    /// <para>
    /// <strong>Raised from 896 once <see cref="StateStoreRemedy"/> was added, because at 896 this
    /// bound truncated on exactly the path the whole feature exists for.</strong> MEASURED, from
    /// the constants themselves: the worst composition is <strong>1081</strong> characters on
    /// Windows (note 360 + remedy 198 + location 76 + tail 438, plus three 3-character joins) and
    /// 976 off it, so a #420 failure whose buffer showed the state-store refusal lost 185
    /// characters. Truncation is from the END, and the end is the TAIL -- whose lines
    /// <see cref="DcpFlightRecorder.Tail"/> selects newest-first precisely because the newest are
    /// the ones nearest the failure. So the cut fell on the most valuable evidence, on the one
    /// occurrence that matters most, and it also converted a path that did not truncate into one
    /// that did -- truncation being one of the transforms that defeats the exact-match secret
    /// ledger downstream.
    /// </para>
    /// <para>
    /// Raising it admits NO additional log volume, which is what makes this the right fix rather
    /// than a weakening: every component is already separately bounded
    /// (<see cref="TailCharLimit"/> caps the tail at 384 characters however large this is), so
    /// this constant only decides whether the already-bounded composition survives intact. The
    /// value carries deliberate headroom over the measured 1081 so a reworded note does not
    /// silently start truncating again -- and
    /// <c>DcpCaptureTests.MaxAnnexLength_CoversTheWorstCaseComposition_SoTheTargetPathNeverTruncates</c>
    /// recomputes the worst case from these constants and reddens if the headroom is ever spent.
    /// </para>
    /// </remarks>
    internal const int MaxAnnexLength = 1152;

    /// <summary>How long the capture write is allowed to take before it is abandoned.</summary>
    /// <remarks>
    /// This write happens on the failure path of a topology start, immediately before
    /// <c>DisposeAsync</c> tears the topology down. An unbounded synchronous write on a stalled
    /// filesystem would block that teardown, and a teardown that does not complete orphans
    /// containers and the session network (section 4.5) -- trading a leaked topology for a
    /// diagnostic is a bad trade in every direction. Five seconds sits well inside the fifteen
    /// second stop budget in <c>HeadlessTopology.DisposeAsync</c>.
    /// </remarks>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>#420's first signature: the throw that reddens the run.</summary>
    internal const string SignatureAddress = "should have valid address";

    /// <summary>#420's second signature: the warning Aspire logs alongside it.</summary>
    /// <remarks>
    /// Aspire's DOWNSTREAM wording, not the fault. It is logged under the
    /// <c>Aspire.Hosting.DistributedApplication</c> category (measured from a live capture, which
    /// is also why the broad <c>Aspire</c>-at-Warning recorder rule exists: the DCP-prefixed rule
    /// alone would not have caught this line). Matching it is still useful, because it is what an
    /// operator sees first.
    /// </remarks>
    internal const string SignaturePort = "Unable to allocate a network port";

    /// <summary>
    /// The known-fault note on Windows, where the fault has been observed and root-caused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Rewritten once the cause was established, and the previous text was worse than
    /// nothing.</strong> It called the fault transient, said it self-clears, and advised a
    /// re-run before investigating. That was the honest reading of the evidence at the time and
    /// it is now known to be wrong: the fault is a DETERMINISTIC refusal, and an elevated
    /// session that hits it will hit it identically forever. Telling such an operator to re-run
    /// costs them two minutes per attempt and never converges.
    /// </para>
    /// <para>
    /// The cause, measured end to end from a capture this recorder took: DCP's controller host
    /// exits with code 1 about 130 ms after start, refusing its state-store directory for
    /// invalid ownership. With the controller dead nothing allocates ports, so Aspire's watch for
    /// the allocation waits on state that never arrives and dies on a fixed 60-second Polly
    /// timeout; two such windows are the constant ~2 minutes an operator sees before the throw.
    /// </para>
    /// </remarks>
    internal const string KnownFaultNoteWindows =
        "known fault (issue 420): DCP's controller could not open its state store and exited, so "
        + "nothing allocated ports and Aspire timed out waiting. NOT transient - re-running will "
        + "fail identically. Check ownership of the state-store directories under your ~/.dcp "
        + "folder; an ELEVATED run uses 'state.elevated'. Remove or re-own the offending one and "
        + "DCP recreates it.";

    /// <summary>
    /// The same signature seen anywhere the fault has never been reproduced.
    /// </summary>
    /// <remarks>
    /// <strong>Still a different note, and for a sharper reason than before.</strong> The
    /// mechanism is a WINDOWS ACL ownership check on the state-store directory, so the remedy
    /// above is Windows-shaped and would be misleading advice elsewhere. #420's own record is
    /// also explicit that the Linux CI runners never reproduced the signature -- 134
    /// container-publishing tests, zero failures, neither string in the job logs. Naming the
    /// resemblance is useful; prescribing a Windows ACL fix off Windows is not.
    /// </remarks>
    internal const string KnownFaultNoteOtherPlatform =
        "issue 420 records this signature, but its known cause is a Windows ownership check on "
        + "DCP's state-store directory and it has never been reproduced off Windows. Treat this "
        + "as a NEW observation: read the capture named above and report it.";

    /// <summary>
    /// The two literals that identify #420's root cause inside the captured buffer.
    /// </summary>
    /// <remarks>
    /// Both measured verbatim from a live capture, in a single DCP line at DEBUG level under the
    /// <c>Aspire.Hosting.Dcp.dcp</c> category: <c>the program finished with an error {"ExitCode":
    /// 1, "error": "failed to initialize state store: could not prepare state store directory
    /// '...\.dcp\state.elevated': ... has invalid ownership: directory owner does not match
    /// current user or token owner"}</c>. Matched as a PAIR rather than singly, so an unrelated
    /// line mentioning either phrase alone cannot trigger the remedy.
    /// </remarks>
    internal const string SignatureStateStore = "state store";

    /// <summary>The second half of the state-store signature. See <see cref="SignatureStateStore"/>.</summary>
    internal const string SignatureOwnership = "invalid ownership";

    /// <summary>
    /// The one-line remedy surfaced when the captured buffer shows the state-store refusal.
    /// </summary>
    /// <remarks>
    /// Deliberately names no absolute path: the capture file holds the exact directory, and an
    /// Environment-error detail reaches public CI artefacts (see <see cref="DescribeLocation"/>).
    /// </remarks>
    internal const string StateStoreRemedy =
        "dcp-cause: DCP refused its state-store directory for invalid ownership and exited, which "
        + "is why nothing allocated a port. Re-own or delete that directory (named in the capture) "
        + "and DCP recreates it.";

    /// <summary>
    /// Whether <paramref name="message"/> carries either of #420's two signatures.
    /// </summary>
    /// <remarks>
    /// Case-insensitive substring matching against the exception message, which is exactly how
    /// every other heuristic in <see cref="OrchestrationErrorClassifier"/> works -- a note that
    /// used a different matching rule from the classifier it enriches would be a second,
    /// divergent classifier.
    /// </remarks>
    internal static bool MentionsKnownFault(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains(SignatureAddress, StringComparison.OrdinalIgnoreCase)
            || message.Contains(SignaturePort, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The known-fault note appropriate to <paramref name="isWindows"/>.</summary>
    internal static string KnownFaultNote(bool isWindows) =>
        isWindows ? KnownFaultNoteWindows : KnownFaultNoteOtherPlatform;

    /// <summary>
    /// Whether the captured buffer shows #420's root cause: DCP refusing its state store for
    /// invalid ownership.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A scan of the BUFFER, deliberately, and not a classifier heuristic on the
    /// exception message.</strong> The evidence exists in exactly one place: a DEBUG-level line
    /// under the <c>Aspire.Hosting.Dcp.dcp</c> category. It is not in
    /// <see cref="Exception.Message"/>, so no message heuristic can ever see it; and it is below
    /// Warning, so the tail that reaches the detail does not carry it either. Scanning here is
    /// the only place the remedy can be recovered from what was captured.
    /// </para>
    /// <para>
    /// <strong>Yes, this is a narrow literal match, and that is the trade taken knowingly.</strong>
    /// If DCP rewords the message it stops matching and the operator gets the capture file and
    /// the troubleshooting guide, exactly as before -- a lost hint, not a wrong one. Against
    /// that: the fault costs a two-minute wait per attempt, produces a message
    /// ("Unable to allocate a network port") that points at the wrong layer entirely, and took a
    /// full day to diagnose the first time. Turning that into one line in the error the operator
    /// is already reading is worth a match that may silently expire.
    /// </para>
    /// </remarks>
    internal static bool MentionsStateStoreRefusal(IReadOnlyList<DcpFlightEntry>? entries)
    {
        if (entries is null)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.Line.Contains(SignatureStateStore, StringComparison.OrdinalIgnoreCase) &&
                entry.Line.Contains(SignatureOwnership, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The capture directory, or <see langword="null"/> when this platform offers no per-user
    /// root to put it in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No temp-directory fallback, and the refusal is the safer answer.</strong>
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns the EMPTY
    /// STRING rather than throwing when a folder is undefined for the platform. The obvious
    /// fallback -- the temp directory -- is the one place this type's own remarks call
    /// world-readable and pre-creatable by another user, and the file name is fully predictable
    /// from a timestamp, so falling back would write host paths and generated credentials to a
    /// path an unprivileged local user could have pre-created as a symlink. A capture is a
    /// convenience; writing it somewhere unsafe is not a convenience. The annex says the file
    /// was not written and why, so the operator is told rather than left guessing.
    /// </para>
    /// <para>
    /// <strong>FULLY QUALIFIED is checked, and the difference from "rooted" is the whole point
    /// (#475's trap).</strong> <see cref="Path.Combine(string, string)"/> on a relative first
    /// segment yields a RELATIVE path, which resolves against the current directory -- under
    /// <c>dotnet test</c> that is inside this repository, where <c>.gitignore</c>'s blacklist
    /// would hide the stray artefact rather than flag it.
    /// <see cref="Path.IsPathRooted(string)"/> does NOT rule that out on Windows, which an
    /// earlier version of this gate assumed: measured, <c>C:captures</c> and <c>\captures</c>
    /// are both rooted=True yet fullyQualified=False, and
    /// <see cref="Path.GetFullPath(string)"/> resolves them against a PER-DRIVE current
    /// directory -- <c>\captures</c> landing on whichever drive the process is on, which under
    /// <c>dotnet test</c> is this repository's. Only
    /// <see cref="Path.IsPathFullyQualified(string)"/> excludes the drive-relative forms, so
    /// that is what this gate and <see cref="NotWrittenReason"/> both use.
    /// </para>
    /// </remarks>
    internal static string? ResolveDirectory(
        string? localApplicationData, string? overrideDirectory = null)
    {
        if (!string.IsNullOrEmpty(overrideDirectory))
        {
            // A SET-BUT-UNUSABLE override is refused, never quietly downgraded to the per-user
            // root. An operator who redirected captures and then finds them in the old place
            // would conclude the redirect worked and the capture never happened, which is the
            // worst of the three possible outcomes; the annex says "not written" instead.
            return Path.IsPathFullyQualified(overrideDirectory)
                ? Path.GetFullPath(overrideDirectory)
                : null;
        }

        if (string.IsNullOrEmpty(localApplicationData) ||
            !Path.IsPathFullyQualified(localApplicationData))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(localApplicationData, DirectoryName));
    }

    /// <summary>The production directory resolution.</summary>
    internal static string? ResolveDirectory() => ResolveDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable(DirectoryOverrideVariable));

    /// <summary>
    /// The capture file name for a moment in time.
    /// </summary>
    /// <remarks>
    /// Millisecond precision and a fixed-width, zero-padded, UTC layout, which makes the names
    /// sort chronologically as ORDINARY STRINGS -- retention can therefore order captures
    /// without reading a single file timestamp, so a copy that preserved modification times
    /// cannot make it prune the wrong file. It is NOT robust against a clock that moved
    /// backwards, which is why <see cref="SelectForDeletion"/> takes the just-written name and
    /// excludes it explicitly rather than trusting the ordering to protect it.
    /// </remarks>
    internal static string BuildFileName(DateTimeOffset utc) =>
        FileNamePrefix
        + utc.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
        + FileNameSuffix;

    /// <summary>
    /// The platform-shaped token naming where a capture lives, WITHOUT the resolved path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The non-Windows token spells out the FALLBACK, and the shorter form was wrong for
    /// the common case.</strong> An earlier version emitted <c>$XDG_DATA_HOME/vouchfx/&lt;name&gt;</c>.
    /// <c>XDG_DATA_HOME</c> is unset on an ordinary Linux desktop and on every CI runner this
    /// repository uses, so that token pasted into a shell expands to <c>/vouchfx/&lt;name&gt;</c> —
    /// a path at the filesystem root, which is not where the file is and is not writable either.
    /// It also contradicted the troubleshooting guide's own table. The token below is the same
    /// rule <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> applies for
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> on Linux — the variable when
    /// set, <c>$HOME/.local/share</c> when not — written in the POSIX default-value form so it
    /// resolves correctly either way in the shell the operator is already in.
    /// </para>
    /// <para>
    /// <strong>macOS is UNVERIFIED and the token is shared with it deliberately.</strong> No macOS
    /// host was available to measure whether .NET consults <c>XDG_DATA_HOME</c> there or returns
    /// <c>$HOME/.local/share</c> unconditionally. If it is the latter, this token still resolves
    /// to the right directory for every operator who has not set the variable — which is the
    /// overwhelming majority, and strictly better than the bare-variable form it replaced, which
    /// was wrong for that same majority on BOTH platforms. An operator who has set it on macOS
    /// would be misdirected, and the troubleshooting guide says so rather than implying a
    /// measurement nobody took.
    /// </para>
    /// <para>
    /// <strong>The resolved path must not reach an Environment-error detail.</strong> That
    /// detail travels into <c>report.html</c> and <c>results.xml</c>, which
    /// <c>vouchfx-run.yml</c> uploads with <c>if: always()</c> -- world-downloadable artefacts on
    /// a public repository -- and an absolute Windows path carries the operator's account name.
    /// The path ledger cannot help: it substitutes DECLARED security material, and a capture
    /// path is not that. This repository already filters Aspire's own "Application host
    /// directory is:" banner for exactly this class of disclosure, so emitting a resolved path
    /// two lines away would be a step backwards. The token is unambiguous to the operator who
    /// needs the file and says nothing about who they are.
    /// </para>
    /// <para>
    /// <strong>That rationale is about THIS string, and it must not be read as a property of the
    /// detail it lands in.</strong> The bounded TAIL travels in the same annex, and it is a
    /// verbatim (ASCII-folded, truncated) copy of what Aspire logged — which routinely contains
    /// absolute host paths, <c>C:\Users\&lt;account&gt;\AppData\Local\Temp\aspire-dcp-*\kubeconfig</c>
    /// among them. Nothing folds a host path out of a tail line. So the account name CAN reach a
    /// public artefact by that route, and emitting a resolved path here would simply be a second,
    /// avoidable way of doing it: the argument for the token is that it costs nothing to withhold
    /// what the operator does not need, not that the surrounding string is clean.
    /// </para>
    /// <para>
    /// Folding host paths out of tail lines was CONSIDERED AND NOT DONE here, deliberately. It is
    /// a change to what the tail reports rather than to what this method emits, it interacts with
    /// the redaction inventory in <c>ScenarioRunner.EnvironmentErrorLine</c>, and a
    /// pattern-matching fold is the kind of transform that quietly mangles the evidence a #420
    /// capture exists to preserve. It belongs in its own change with its own drills; naming the
    /// exposure here is what stops it being forgotten.
    /// </para>
    /// </remarks>
    internal static string DescribeLocation(
        string fileName, bool isWindows, DcpCaptureRoot root = DcpCaptureRoot.PerUser)
    {
        switch (root)
        {
            // When the operator redirected captures, naming the DEFAULT location is a falsehood
            // in exactly the direction ResolveDirectory's own remark calls the worst outcome - it
            // just points the wrong way instead of the wrong way round. It also misdirects
            // precisely on the CI path the troubleshooting guide recommends, which is the one
            // place nobody can check by looking. Still a token, never the resolved path, so the
            // account name stays out of a public artefact.
            case DcpCaptureRoot.EnvironmentOverride:
                return isWindows
                    ? "%" + DirectoryOverrideVariable + "%\\" + fileName
                    : "$" + DirectoryOverrideVariable + "/" + fileName;

            // A directory handed straight to the flush by an in-process caller. NO root token can
            // name it: both of the others name an environment variable, and emitting one that is
            // NOT SET is the same misdirection class this method exists to avoid - a token the
            // operator pastes into a shell and watches expand to nothing, or worse to the
            // filesystem root. The bare name claims only what is true, which is that a capture
            // under this name was written where the host asked for it.
            case DcpCaptureRoot.HostSupplied:
                return fileName;

            case DcpCaptureRoot.PerUser:
            default:
                return isWindows
                    ? "%LOCALAPPDATA%\\" + DirectoryName + "\\" + fileName
                    : UnixRootToken + "/" + DirectoryName + "/" + fileName;
        }
    }

    /// <summary>
    /// Which of <paramref name="fileNames"/> retention should delete, keeping the
    /// <paramref name="retain"/> newest and never the one just written.
    /// </summary>
    /// <param name="fileNames">Capture file names (not paths), in any order.</param>
    /// <param name="retain">How many to keep. Zero or less keeps none.</param>
    /// <param name="justWritten">
    /// The name written moments ago, which is never a deletion candidate whatever the ordering
    /// says. Ordering is by NAME, and a name is a timestamp: a host clock that stepped backwards
    /// makes the newest file sort oldest, and retention would then delete the capture the
    /// current failure just produced -- the one occasion on which losing a capture costs
    /// everything. Pass <see langword="null"/> when pruning outside a write.
    /// </param>
    internal static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<string> fileNames, int retain, string? justWritten)
    {
        ArgumentNullException.ThrowIfNull(fileNames);

        if (retain < 0)
        {
            retain = 0;
        }

        // The just-written capture is excluded from the deletion candidates but still COUNTS
        // against the budget, so retain means "capture files left on disk" rather than "others
        // left beside the new one". Getting this wrong leaves retain + 1 files and quietly makes
        // every stated retention figure off by one.
        var candidates = fileNames
            .Where(n => !string.Equals(n, justWritten, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Reserved is decided by whether a capture WAS just written, not by whether the listing
        // happened to include it. Deriving it from a count comparison meant a listing that missed
        // the new file - a directory enumeration taken before the write landed, a filesystem that
        // does not reflect it yet - reserved nothing and left retain + 1 files on disk, quietly
        // making every stated retention figure off by one in exactly the case retention is under
        // most pressure. The file exists either way: this method's caller has just created it.
        var reserved = justWritten is null ? 0 : 1;

        return candidates
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .Skip(Math.Max(0, retain - reserved))
            .ToArray();
    }

    /// <summary>
    /// The seamed write: creates the directory, writes the capture under a name that does not
    /// already exist, then prunes older captures.
    /// </summary>
    /// <returns>
    /// The file NAME written, or <see langword="null"/> when nothing was written. Never throws:
    /// a diagnostic that can break the run it is diagnosing is worse than no diagnostic.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Returns the name rather than the path deliberately -- see
    /// <see cref="DescribeLocation"/> for why the resolved path must not travel onward.
    /// </para>
    /// <para>
    /// <paramref name="createFile"/> is expected to fail when the file already exists (the
    /// production implementation passes <c>FileMode.CreateNew</c>), which is what makes the
    /// suffix retry below correct rather than decorative: two failures inside the same
    /// millisecond otherwise silently overwrite, and the one lost would be the earlier -- the
    /// first occurrence in a burst, which is the most interesting one.
    /// </para>
    /// <para>
    /// Retention runs AFTER the write and is separately guarded, so a failure to delete an old
    /// capture -- a file held open by an editor, say -- still leaves the new capture in place and
    /// still reports its name. Losing the pruning is a housekeeping cost; losing the capture is
    /// the whole point of the change.
    /// </para>
    /// </remarks>
    internal static string? WriteCore(
        string directory,
        string fileName,
        string content,
        int retain,
        Action<string> createDirectory,
        Func<string, string, bool> createFile,
        Func<string, IReadOnlyList<string>> listCaptureFileNames,
        Action<string> deleteFile)
    {
        ArgumentNullException.ThrowIfNull(createDirectory);
        ArgumentNullException.ThrowIfNull(createFile);
        ArgumentNullException.ThrowIfNull(listCaptureFileNames);
        ArgumentNullException.ThrowIfNull(deleteFile);

        string written;
        try
        {
            createDirectory(directory);

            var candidate = fileName;
            var attempt = 1;
            while (!createFile(Path.Combine(directory, candidate), content))
            {
                attempt++;
                if (attempt > 5)
                {
                    return null;
                }

                candidate = Path.GetFileNameWithoutExtension(fileName)
                    + "-" + attempt.ToString(CultureInfo.InvariantCulture)
                    + FileNameSuffix;
            }

            written = candidate;
        }
        catch (Exception)
        {
            // Deliberate, documented discard, and the same discipline as the teardown stop in
            // HeadlessTopology.DisposeAsync: this runs on the failure path of a topology start,
            // where the caller is already carrying the real exception and is about to rethrow
            // it. A disk-full or permission failure here must not replace that exception with
            // one about the diagnostic.
            return null;
        }

        try
        {
            foreach (var stale in SelectForDeletion(
                listCaptureFileNames(directory), retain, written))
            {
                deleteFile(Path.Combine(directory, stale));
            }
        }
        catch (Exception)
        {
            // See above. The capture is already on disk; pruning is best-effort.
        }

        return written;
    }

    /// <summary>The production write, against the real filesystem, bounded in time.</summary>
    /// <returns>The file NAME written, or <see langword="null"/>.</returns>
    internal static async Task<string?> WriteAsync(
        string content,
        DateTimeOffset utc,
        string? directory = null)
    {
        var target = directory ?? ResolveDirectory();
        if (target is null)
        {
            return null;
        }

        // Bounded (section 4.5): the write sits immediately before topology teardown, so a
        // stalled filesystem must cost a bounded wait and a missing capture, never a blocked
        // teardown and orphaned containers. Task.WhenAny abandons the write rather than
        // cancelling the I/O -- a synchronous file write is not reliably interruptible -- which
        // is the honest trade: control returns on time, the abandoned task finishes or does not,
        // and nothing downstream waits on it.
        var write = Task.Run(
            () => WriteCore(
                target,
                BuildFileName(utc),
                content,
                RetainedFiles,
                CreateDirectoryOwnerOnly,
                CreateFileOwnerOnly,
                ListCaptureFileNames,
                File.Delete));

        // The timer is cancelled the moment the write wins, so the five-second callback is not
        // left queued on the timer wheel for a write that finished in a millisecond. Every
        // topology start pays this, and a system timer nobody will ever look at is exactly the
        // kind of residue a diagnostic must not leave behind. Cancelling a Task.Delay leaves it
        // in the Canceled state, which raises no unobserved-exception event, so the abandoned
        // task on the other branch stays as harmless as it already was.
        using var timeoutCts = new CancellationTokenSource();

        var finished = await Task.WhenAny(write, Task.Delay(WriteTimeout, timeoutCts.Token))
            .ConfigureAwait(false);

        if (!ReferenceEquals(finished, write))
        {
            return null;
        }

        await timeoutCts.CancelAsync().ConfigureAwait(false);

        try
        {
            return await write.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the capture directory, owner-only on platforms that have file modes.
    /// </summary>
    /// <remarks>
    /// A capture holds generated dependency passwords and absolute host paths from this
    /// machine. On a multi-user host the default directory mode would leave both readable by
    /// every local account, which is a needless disclosure for a file only its owner will ever
    /// use. The precedent is <c>TestCertificateAuthority.RestrictToOwner</c>, which restricts
    /// private-key material the same way and for the same reason. Windows inherits the
    /// per-user root's ACL and needs no equivalent.
    /// </remarks>
    private static void CreateDirectoryOwnerOnly(string directory)
    {
        // Checked BEFORE creating, because CreateDirectory reports success either way and this
        // is the only moment the difference is knowable.
        var alreadyExisted = Directory.Exists(directory);

        var created = Directory.CreateDirectory(directory);

        // Only a directory THIS call created gets its mode narrowed. Chmodding one the operator
        // already had is wrong twice over: it silently changes permissions on something outside
        // this feature's ownership - contradicting the documented promise that a directory you
        // name keeps the permissions it has - and on a directory this process does not own it
        // THROWS, which WriteCore's blanket catch would swallow into a bare "not written" with
        // no reason attached. A silent refusal is the one failure mode a diagnostic must not
        // have.
        if (!alreadyExisted && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                created.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Creates the capture file exclusively and writes it, owner-only where modes exist.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the name is already taken, so the caller can pick another;
    /// any other failure throws and is handled by <see cref="WriteCore"/>.
    /// </returns>
    /// <remarks>
    /// <c>FileMode.CreateNew</c> rather than <c>File.WriteAllText</c>: exclusive creation is
    /// what makes the same-millisecond collision detectable instead of silently destructive, and
    /// on a shared host it is also what stops a pre-created path being written through.
    /// </remarks>
    private static bool CreateFileOwnerOnly(string path, string content)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Narrowly scoped to the CREATION: a write failure after this point is a real
            // failure and must propagate to WriteCore, not be mistaken for a name collision and
            // retried under a different name.
            return false;
        }

        using (stream)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // UTF8Encoding(false), NOT Encoding.UTF8: the latter's encoder emits a BOM, so the
            // capture began EF BB BF and its first "line" was not the header this type writes.
            // MEASURED at position 0 of a real capture. It contradicts the file's own
            // printable-ASCII, one-character-one-byte premise, and it silently breaks the obvious
            // triage move - grepping the first line for the header, or for `vouchfx DCP flight`
            // anchored at the start.
            using var writer = new StreamWriter(
                stream,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                4096,
                leaveOpen: true);
            writer.Write(content);
        }

        return true;
    }

    private static IReadOnlyList<string> ListCaptureFileNames(string directory) =>
        Directory
            .EnumerateFiles(directory, FileNamePrefix + "*" + FileNameSuffix)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

    /// <summary>
    /// The one-line summary that travels on the exception: where the capture went, and the tail
    /// that has to survive without it.
    /// </summary>
    /// <param name="location">
    /// The platform token from <see cref="DescribeLocation"/>, or <see langword="null"/> when no
    /// file was written.
    /// </param>
    /// <param name="tail">The bounded tail from <see cref="DcpFlightRecorder.Tail"/>.</param>
    /// <param name="notWrittenReason">Short reason to report when nothing was written.</param>
    /// <remarks>
    /// <strong>The tail's redaction is BEST-EFFORT, and calling it anything stronger would be
    /// wrong.</strong> It is composed into <c>Detail</c> and so passes through
    /// <c>ScenarioRunner.EnvironmentErrorLine</c> like any other environment-error detail -- but
    /// it arrives there as a MUTATED copy of what Aspire logged: truncated to
    /// <see cref="DcpFlightRecorder.TailLineChars"/> per line and to
    /// <see cref="MaxAnnexLength"/> overall, and with every non-ASCII character replaced. The
    /// ledger redacts EXACT occurrences, so any of those transforms defeats it for the value it
    /// touched. That is a known and accepted property of this path, enumerated alongside the
    /// other three truncation sites in <c>EnvironmentErrorLine</c>'s own remarks, and it is why
    /// the tail is warning-level lines only rather than the whole buffer.
    /// </remarks>
    internal static string BuildSummary(
        string? location,
        IReadOnlyList<string>? tail,
        string? notWrittenReason = null,
        bool stateStoreRefusal = false)
    {
        var parts = new List<string>(3);

        // FIRST, ahead of the location and the tail. When it fires it is the only line that
        // tells the operator what to DO, and the annex is length-bounded from the end - so a
        // remedy placed after a long tail is the part that gets truncated away.
        if (stateStoreRefusal)
        {
            parts.Add(StateStoreRemedy);
        }

        parts.Add(location is null
            ? "dcp-capture: not written"
                + (string.IsNullOrEmpty(notWrittenReason) ? string.Empty : " (" + notWrittenReason + ")")
            : "dcp-capture: " + location);

        if (tail is { Count: > 0 })
        {
            parts.Add("dcp-tail: " + string.Join(" ;; ", tail));
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Records <paramref name="summary"/> on <paramref name="exception"/> so it reaches the
    /// classifier without altering the exception's type, message or stack trace.
    /// </summary>
    /// <remarks>
    /// <see cref="Exception.Data"/> can be read-only or absent on an exotic exception type, and
    /// a non-serialisable value can throw on assignment; both are swallowed on the same grounds
    /// as the write above.
    /// </remarks>
    internal static void Attach(Exception exception, string summary)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var data = exception.Data;
            if (data is null || data.IsReadOnly)
            {
                return;
            }

            data[DataKey] = summary;
        }
        catch (Exception)
        {
            // See WriteCore: never replace the real failure with one about the diagnostic.
        }
    }

    /// <summary>The summary previously attached to <paramref name="exception"/>, if any.</summary>
    internal static string? Read(Exception? exception)
    {
        try
        {
            return exception?.Data?[DataKey] as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Everything this type contributes to an Environment-error detail for one failure:
    /// the known-fault note when the signature matches, and the capture summary when one was
    /// attached. Bounded by <see cref="MaxAnnexLength"/>.
    /// </summary>
    /// <param name="message">The exception message being classified.</param>
    /// <param name="exception">The exception, possibly carrying an attached summary.</param>
    /// <param name="isWindows">
    /// Which known-fault note applies. Taken as an argument rather than read from the ambient
    /// platform so the drills can pin BOTH notes on one machine -- a platform-conditional string
    /// that only one CI leg can ever exercise is a string nobody checks.
    /// </param>
    /// <returns>The annex, or an empty string when there is nothing to add.</returns>
    internal static string BuildAnnex(string? message, Exception? exception, bool isWindows)
    {
        var parts = new List<string>(2);

        if (MentionsKnownFault(message))
        {
            parts.Add(KnownFaultNote(isWindows));
        }

        var summary = Read(exception);
        if (!string.IsNullOrEmpty(summary))
        {
            parts.Add(summary!);
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var annex = string.Join(" | ", parts);
        return annex.Length <= MaxAnnexLength
            ? annex
            : string.Concat(annex.AsSpan(0, MaxAnnexLength), "...");
    }

    /// <summary>
    /// Why no capture was written, when the reason is one the operator can act on.
    /// </summary>
    /// <remarks>
    /// Only the two DIRECTORY refusals get a reason, because those are the two an operator
    /// caused and can undo. A disk-full or permission failure inside the write reports the plain
    /// "not written": guessing at its cause in an event field would be inventing a diagnosis.
    /// </remarks>
    internal static string? NotWrittenReason(string? writtenName, string? directoryArgument)
    {
        if (writtenName is not null || directoryArgument is not null)
        {
            return null;
        }

        var configured = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            return Path.IsPathFullyQualified(configured)
                ? null
                : DirectoryOverrideVariable + " is not an absolute path";
        }

        return ResolveDirectory() is null
            ? "this platform defines no per-user directory to write it to safely"
            : null;
    }

    /// <summary>
    /// The whole failure-path flush: write the capture, attach the summary, drop the recorder.
    /// </summary>
    /// <param name="recorder">The recorder armed for this topology.</param>
    /// <param name="exception">The exception about to be rethrown.</param>
    /// <param name="utc">The moment of the failure.</param>
    /// <param name="directory">Capture directory override; production passes none.</param>
    /// <remarks>
    /// <para>
    /// Disposes the recorder in a <c>finally</c>: whether or not the capture could be written,
    /// this topology's arming window is over and the buffer must not outlive it. Calling it
    /// twice is harmless -- the second call finds an empty, already-dropped recorder -- which is
    /// what lets both the health-gate catch and the outer safety net call it.
    /// </para>
    /// </remarks>
    internal static async Task FlushOnFailureAsync(
        DcpFlightRecorder recorder,
        Exception exception,
        DateTimeOffset utc,
        string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(exception);

        if (recorder.IsDropped)
        {
            return;
        }

        try
        {
            var tail = recorder.Tail(TailEntryLimit, TailCharLimit);
            var refusal = MentionsStateStoreRefusal(recorder.Snapshot());
            var body = recorder.FormatCapture(utc);

            var name = await WriteAsync(body, utc, directory).ConfigureAwait(false);

            // Which root actually holds the file, decided in the one place that knows both halves.
            //
            // THE ARGUMENT WINS, and the order here is not free choice - it must match WriteAsync,
            // which resolves its target as `directory ?? ResolveDirectory()`. An earlier version
            // had the environment variable outrank the argument, so with BOTH set the file landed
            // in the argument's directory while this token named %VOUCHFX_DCP_CAPTURE_DIR%: the
            // precise misdirection DescribeLocation exists to prevent, committed by its own
            // caller. Unreachable in production - no production call site passes a directory - but
            // two functions disagreeing about which input wins is a defect whether or not anything
            // reaches it today.
            //
            // A directory passed with no variable set gets neither token; see
            // DcpCaptureRoot.HostSupplied for why the bare name is the only honest answer.
            var root = directory is not null
                ? DcpCaptureRoot.HostSupplied
                : !string.IsNullOrEmpty(
                        Environment.GetEnvironmentVariable(DirectoryOverrideVariable))
                    ? DcpCaptureRoot.EnvironmentOverride
                    : DcpCaptureRoot.PerUser;

            var location = name is null
                ? null
                : DescribeLocation(name, OperatingSystem.IsWindows(), root);

            Attach(exception, BuildSummary(
                location, tail, NotWrittenReason(name, directory), refusal));
        }
        catch (Exception)
        {
            // See WriteCore.
        }
        finally
        {
            recorder.Dispose();
        }
    }
}
