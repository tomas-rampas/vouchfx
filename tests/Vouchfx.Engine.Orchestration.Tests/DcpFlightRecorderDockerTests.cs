using Microsoft.Extensions.DependencyInjection;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// The three assertions the #420 flight recorder needs a real topology for: that a start which
/// SUCCEEDS leaves nothing behind, that the filter rules survive contact with the real Aspire
/// host, and that a FAILING topology writes a capture holding real DCP traffic through the
/// production flush path. Four Docker-free rows sit beside them and pin the helpers the Docker
/// rows lean on, in the blocking lane: the ownership filter on production-named files, and the
/// failure-message description on real formatter output.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about the recorder is pinned by fast drills against injected seams, because
/// the fault it captures is not reproducible on demand. The three Docker rows cannot be: each is
/// a claim about the production wiring inside a running Aspire host.
/// </para>
/// <para>
/// <strong>Each Docker row states its own relationship to the operator's REAL capture
/// directory, because they differ and the difference is deliberate</strong> (the Docker-free
/// rows never touch it: one owns a scratch directory of its own, the other three touch no
/// directory at all):
/// </para>
/// <list type="bullet">
///   <item><c>StartAsync_WhenTheTopologyComesUp_WritesNoCaptureFile</c> runs ARMED and against
///   the real directory, because disarming or redirecting it would turn its assertion - that a
///   successful start writes nothing - into a tautology. It deletes the captures it can prove
///   are ITS OWN, so a flaky Docker leg cannot leave an artefact a later reader mistakes for a
///   real finding - and deletes nothing else.</item>
///   <item><c>Register_InsideTheRealAspireHost_...</c> is about log routing and has no business
///   writing captures at all, so it disarms the production recorder with
///   <c>VOUCHFX_DCP_CAPTURE=0</c> and brings its own. It proves the disarm held by asking the
///   production decision directly rather than by counting files - a favourable trade rather than
///   a strict improvement, argued at that row, which is also the one row that cannot key on a
///   resource name.</item>
///   <item><c>AFailingTopology_WritesACaptureIntoTheRedirectedDirectory</c> must write a
///   capture - that is its property - so it REDIRECTS the production path to a scratch
///   directory with <c>VOUCHFX_DCP_CAPTURE_DIR</c> and asserts that no capture OF ITS OWN
///   appeared in the real directory. Redirecting rather than reaching past the production code is
///   what keeps it a test of the real flush, and it is the row that proves the arming window
///   spans the health gates.</item>
/// </list>
/// <para>
/// <strong>Why every one of those is phrased "its own" rather than "nothing new" - issue
/// #489.</strong> The capture directory <see cref="DcpCapture.ResolveDirectory()"/> resolves is
/// per-USER (<c>LocalApplicationData</c>), so every process this user runs shares it. These rows
/// used to assert that the directory did not GROW during their window, which is a claim about
/// the whole machine and not about the code under test. It is false, and #489 is what it looks
/// like when it fails: expected 5, actual 6, because something else wrote a capture while this
/// row was running.
/// </para>
/// <para>
/// <strong>The serialisation premise those assertions rested on does not hold, MEASURED.</strong>
/// From the integration job of CI run 33904509538 (2026-09-04) - the run that produced #489 -
/// <strong>37</strong> test hosts announced "Test run for ..." between 18:22:01.481Z and
/// 18:23:09.437Z, a 68-second span. Each duration below is given with its KIND, because the two
/// kinds differ and quoting them interchangeably is what made an earlier version of this
/// paragraph contradict itself:
/// </para>
/// <list type="bullet">
///   <item><c>Vouchfx.Engine.Orchestration.Tests</c> - banner 18:22:01.728Z to summary
///   19:05:59.833Z = <strong>43m58s wall clock</strong>; VSTest reported 43m27s of test
///   execution on its summary line.</item>
///   <item><c>Vouchfx.Engine.Runtime.Tests</c> - banner 18:22:10.137Z to summary 18:36:35.436Z =
///   <strong>14m25s wall clock</strong>; VSTest reported 13m25s of test execution.</item>
/// </list>
/// <para>
/// Runtime's window sits entirely inside Orchestration's, so the OVERLAP IS Runtime's window:
/// 14m25s. The #489 failure - <c>Assert.Equal() Failure … Expected: 5 / Actual: 6</c> - is
/// logged on the <c>Failed</c> line at 18:29:23.679Z (the xUnit progress line precedes it at
/// 18:29:23.676Z), inside that overlap. 37 hosts announcing inside 68 seconds while two of them
/// run for 14 and 44 minutes makes sequential per-project execution arithmetically impossible;
/// that, not any single duration, is the decisive fact.
/// </para>
/// <para>
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c> serialises rows
/// WITHIN one assembly; it says nothing about other processes.
/// <c>Environment.SetEnvironmentVariable</c> is process-scoped for the same reason, so this
/// class's redirect covers one writer out of several: <c>Vouchfx.Engine.Runtime.Tests</c> carries
/// secured-probe-abort rows that write captures, and some of those spawn <c>vouchfx run</c> CLI
/// subprocesses, a third writer class no in-process redirect can reach.
/// </para>
/// <para>
/// So no in-process discipline can make "nobody else writes here" true, and these rows do not
/// assert it. The two rows that declare a resource identify THEIR OWN capture by the name they
/// splice into their topology - a name made unique PER ROW RUN, not per class, so two hosts
/// running this same row on one account cannot claim each other's files - which DCP echoes into
/// the traffic the capture holds. The routing row declares none, so it cannot key on anything and
/// does not try; it proves the same property at its source instead. Each assertion argues its own
/// case where it stands.
/// </para>
/// <para>
/// All three restore whatever environment variable they set, and this assembly disables test
/// parallelism, so the process-wide mutation cannot race a sibling row IN THIS PROCESS - which
/// is all that claim ever covered.
/// </para>
/// <para>
/// <strong>No UNRESOLVED note here, deliberately, and the two AssemblyInfo files carry one.</strong>
/// The open gap those files record is the serialisation LEVER (#525) - other assemblies'
/// topologies can still start alongside this one's, and the DCP ~20s startup-watchdog concern is
/// therefore only half contained. Nothing in this file turns on that: these rows are made sound
/// by owning what they assert on, which holds whether or not the lever is ever pulled. Should
/// the lever change, nothing here needs revisiting.
/// </para>
/// </remarks>
public sealed class DcpFlightRecorderDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>
    /// The container <see cref="StartAsync_WhenTheTopologyComesUp_WritesNoCaptureFile"/> splices
    /// into its topology, and therefore the token that identifies a capture as THAT ROW RUN's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One field, read by both the splice and the ownership test, so the two cannot drift: a
    /// divergence would leave the row silently unable to recognise - or delete - its own
    /// artefacts.
    /// </para>
    /// <para>
    /// <strong>Per RUN, not per class, and the suffix is load-bearing.</strong> xUnit builds a
    /// fresh instance of this class for every row execution, so this Guid is unique to one
    /// execution in one process. A class constant would have been a key to "a topology of my
    /// KIND" rather than "my topology": two hosts running this same row on one account write
    /// captures carrying the same token, so each host's ownership filter claims BOTH files and
    /// whichever reaches its cleanup first deletes the other's. The harm is precise and is the
    /// bug being fixed, narrowed rather than closed - if the other process's start had regressed
    /// and flushed a real capture, this row would delete it and that process would find nothing
    /// and pass GREEN. Unreachable on today's CI (one job, one ephemeral runner, intra-assembly
    /// parallelism off) and two developers are two <c>LOCALAPPDATA</c> roots; reachable for one
    /// developer running two test hosts on one account, or a self-hosted runner taking concurrent
    /// jobs.
    /// </para>
    /// <para>
    /// Eight lowercase hex characters, so both names stay lowercase-alphanumeric-and-hyphen and
    /// well under thirty characters - the conservative shape for a container or service name
    /// rather than one probed against a documented limit. Measured, not assumed: both topologies
    /// start and reach their intended outcome with the suffix applied, and the suffixed name
    /// reaches the capture body (assertion (c) in the failing row asserts exactly that).
    /// </para>
    /// </remarks>
    private readonly string _healthyResourceName =
        "whoami-dcp-capture-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The service <see cref="AFailingTopology_WritesACaptureIntoTheRedirectedDirectory"/>
    /// splices into its topology, and therefore that row run's ownership key. Same reasoning as
    /// <see cref="_healthyResourceName"/>, including the per-run suffix.
    /// </summary>
    private readonly string _failingResourceName =
        "never-ready-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A topology that comes up writes no capture: the buffer is dropped, not flushed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This row runs ARMED, against the operator's REAL capture directory, and that is a
    /// deliberate choice rather than an oversight — it has already been read as one, so it is
    /// stated here as well as in the class header.</strong> The two safer-looking alternatives
    /// both destroy the assertion. Disarming with <c>VOUCHFX_DCP_CAPTURE=0</c> means no recorder
    /// is created at all, so "no capture appeared" becomes true by construction and the row proves
    /// nothing about the READY path dropping the buffer. Redirecting with
    /// <c>VOUCHFX_DCP_CAPTURE_DIR</c> is weaker in a subtler way: it would still exercise the drop,
    /// but it could no longer catch a regression that writes to the DEFAULT root — which is the
    /// location an operator would actually find a spurious file in.
    /// </para>
    /// <para>
    /// The cost of that choice is that a failing Docker leg could leave an artefact in a real
    /// directory whose entire value is that a file in it means something. So the row, in a
    /// <c>finally</c> — whether it passed, failed or threw — deletes the files it can prove ARE
    /// ITS OWN, one at a time, so a single locked capture does not abandon the rest.
    /// </para>
    /// <para>
    /// Either way the names reach the failure message, and that is what keeps the evidence alive
    /// across the cleanup: the ones it deleted, so the finding survives the file that carried it,
    /// and the ones it could NOT delete, flagged with the exception type and an instruction to
    /// remove them by hand — because those are still sitting in the operator's directory, where
    /// the next reader will take them for a real finding.
    /// </para>
    /// <para>
    /// <strong>"Its own", not "everything new", and that narrowing is a bug fix rather than
    /// caution.</strong> This row used to delete every capture that appeared during its window.
    /// The window overlaps other test hosts (see the class remarks for the measurement), so a
    /// genuine capture written by a concurrent <c>Vouchfx.Engine.Runtime.Tests</c> probe-abort —
    /// a file this row does not own, in a directory whose whole value is that a file in it is a
    /// real finding — was destroyed by this row, which then reddened blaming its own topology.
    /// Both halves of that are now keyed on ownership: it asserts on its own captures and it
    /// deletes only its own captures.
    /// </para>
    /// <para>
    /// The sibling rows differ, and the class header's list says how: the routing row disarms
    /// because it has no business writing captures, and the failing-topology row redirects because
    /// it must write one.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task StartAsync_WhenTheTopologyComesUp_WritesNoCaptureFile()
    {
        // Arrange - the operator's real capture directory, resolved exactly as production
        // resolves it. It may not exist at all, which is the ordinary case on a machine that has
        // never met #420.
        var directory = DcpCapture.ResolveDirectory();
        Assert.NotNull(directory);

        var mine = Array.Empty<string>();
        string? listFault = null;
        var undeleted = new List<string>();
        try
        {
            // Act - a real, successful topology start through the production path, with the
            // recorder ARMED. Disarming here would turn the assertion below into a tautology.
            await using (var topology = await HeadlessTopology.StartAsync(
                appHostAssemblyName: AppHostAssemblyName,
                configureResources: b => b.AddContainer(_healthyResourceName, "traefik/whoami")))
            {
                Assert.NotNull(topology.Application);
            }
        }
        finally
        {
            // Whatever happened, leave the operator's directory as this row found it, less this
            // row run's own leavings and nothing else. CapturesNaming applies the ownership
            // key; its own comments argue what that key does and does not establish.
            //
            // Nothing here may throw, and NOT because a fault is unimportant. This is a finally:
            // a throw escaping it REPLACES the topology exception the row is being run to
            // diagnose. Listing a directory is TOCTOU-prone (see ListCaptures) and File.Delete
            // races retention, so the throw is reachable on both lines. Each fault is recorded
            // and reported after the try instead, so the primary exception survives when there is
            // one and a cleanup that could not run still reddens the row.
            //
            // THE TWO FAULTS ARE RECORDED SEPARATELY, because they mean opposite things and an
            // earlier version of this row collapsed them into one message that was false in one
            // of the two cases. A listing that failed leaves this row unable to say ANYTHING
            // about what it left behind. A delete that failed means the row has already FOUND a
            // capture naming its own resource - the very regression it exists to catch - and
            // knows the filename; the only thing it could not do is tidy up. Reporting that as
            // "cannot say whether it left an artefact behind" would file a real finding under
            // infrastructure trouble, which is the section-12.1 conflation this project refuses
            // to make about verdicts, committed here about a test report.
            try
            {
                mine = CapturesNaming(directory!, _healthyResourceName);
            }
            catch (IOException ex)
            {
                listFault = ex.GetType().Name;
            }
            catch (UnauthorizedAccessException ex)
            {
                listFault = ex.GetType().Name;
            }

            foreach (var stray in mine)
            {
                // Per file, so one locked capture does not abandon the rest. Exception TYPE and
                // bare filename only: the message of an IOException from File.Delete carries the
                // resolved path, and the sibling failing row asserts at (d) that the ENGINE must
                // keep resolved paths out of a diagnostic. This row holds itself to the rule it
                // holds the engine to.
                try
                {
                    File.Delete(stray);
                }
                catch (IOException ex)
                {
                    undeleted.Add(Path.GetFileName(stray) + " (" + ex.GetType().Name + ")");
                }
                catch (UnauthorizedAccessException ex)
                {
                    undeleted.Add(Path.GetFileName(stray) + " (" + ex.GetType().Name + ")");
                }
            }
        }

        // Assert - nothing of this row's own. A healthy run pays a bounded in-memory buffer for
        // the duration of the start and the health gates, and nothing else: no file, no output,
        // no residue.
        //
        // Property 1 holds for the shape this row exists to catch: a capture written because a
        // SUCCESSFUL start flushed instead of dropping is written after DCP created the
        // container, so the buffer it holds names _healthyResourceName and nothing else on the
        // machine can.
        //
        // THAT THE RESOURCE NAME REACHES A CAPTURE BODY AT ALL IS ARGUED HERE, MEASURED NEXT
        // DOOR. Assertion (c) in the sibling failing row pins it on a real capture in this same
        // docker leg, so the key cannot go silently vacuous - but that capture differs from this
        // row's hypothetical one in three ways: a topology that FAILED rather than succeeded, a
        // different resource name, and the gate-failure flush site rather than a success-path
        // one. What carries across the three is the mechanism, not the circumstance: the name is
        // echoed by DCP's own reconciler traffic as it brings a resource up, which both
        // topologies do identically and neither outcome is a precondition for. What does NOT
        // carry across is timing, and that is the limit below.
        //
        // KNOWN LIMIT, stated rather than papered over, and it is a FAMILY rather than one case.
        // Measured on real captures from this host, the name arrives only at the very end of the
        // buffer: one 20-entry capture names its resource on entries 18 and 19 only, and the
        // capture the property-1 drill produced held 18 entries with 2 naming theirs (a reviewer
        // counting nine captures got 3 of 20, first at entry 17 - same shape). Everything DCP
        // logs before its reconcilers run - apiserver start, controller host, kubeconfig read,
        // network creation, image pull - is nameless. So ANY failure before that point - the
        // #420 shape among them, but equally an image-pull failure, a network-creation failure,
        // or an apiserver or controller death - leaves a capture this row cannot claim, and it is
        // therefore neither asserted on nor deleted. The row still goes red, on the
        // Assert.NotNull/throw above rather than here, and the file stays put. That is the safe
        // direction: an unclaimable capture in this directory is indistinguishable from a real
        // finding, and deleting it on suspicion is exactly the bug fixed here.
        //
        // A SECOND, NARROWER SILENT MISS, on the other side of the listing. Retention keeps
        // DcpCapture.RetainedFiles = 12 files and each write prunes the oldest, so if twelve
        // captures were written by OTHER hosts after this row's own flush and before the listing
        // below, this row's file is gone before it can be seen: `mine` comes back empty and the
        // row passes green on a real leak. It needs twelve concurrent writers inside one row's
        // window, which nothing observed comes close to - but it is the #489 class stated in this
        // fix's own terms, and every other residual here is stated.
        //
        // FINDING FIRST, HYGIENE SECOND, and the order is the point rather than style. A
        // populated `mine` is the regression; an unfinished cleanup is trouble tidying up after
        // it. Asserting the cleanup first would let a locked capture - reachable, see
        // CapturesNaming's remarks on DcpCapture.WriteAsync - report a real engine regression
        // as an infrastructure problem, and the reader would never see the filename.
        //
        // Composed on the failing path only. Assert.True(cond, message) builds its message on
        // every pass; here that meant rendering a sentence about files that do not exist on every
        // green run.
        if (mine.Length > 0)
        {
            Assert.Fail(
                "A successful topology start wrote " + mine.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + " DCP capture file(s) naming this run's own resource '" + _healthyResourceName
                + "', which only the FAILURE path may do:\n  "
                + string.Join("\n  ", mine.Select(Path.GetFileName))
                + (undeleted.Count == 0
                    ? "\nThey have been deleted again so they cannot be mistaken for a real "
                        + "finding."
                    : "\nThese could NOT be deleted and are still in the operator's capture "
                        + "directory, where a later reader will take them for a real finding - "
                        + "remove them by hand:\n  " + string.Join("\n  ", undeleted)));
        }

        if (listFault is not null)
        {
            Assert.Fail(
                "the capture directory could not be listed (" + listFault + "), so this row "
                + "cannot say whether it left an artefact behind. Nothing was deleted.");
        }
    }

    /// <summary>
    /// The filter rules survive contact with the real Aspire host: DCP traffic reaches a recorder
    /// registered through <see cref="DcpFlightRecorder.Register"/>, and nothing else does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fast drill for these rules builds a bare <c>LoggerFactory</c>, which proves the rule
    /// SELECTION but not that Aspire's own logging configuration leaves it alone - a host that
    /// loaded its own filter rules from configuration, or cleared providers after this
    /// registration, would break the recorder silently while every unit drill stayed green. That
    /// is the gap this closes, and only a real host can.
    /// </para>
    /// <para>
    /// It registers its OWN recorder through the production registration method rather than
    /// reaching for the one <see cref="HeadlessTopology.StartAsync"/> creates, and disarms that
    /// one for the duration: this drill is about routing, so it has no business writing into the
    /// operator's capture directory if the start happens to fail.
    /// </para>
    /// <para>
    /// <strong>This is the one row that cannot key its capture-directory claim on a resource
    /// name, and the reason is structural: it declares no resources at all.</strong> Its
    /// <c>configureResources</c> callback only adds a logging registration, so a capture written
    /// by this row would hold DCP traffic about a resource-less topology - nothing in it
    /// distinguishes it from a capture a concurrent test host wrote. It therefore cannot make
    /// ANY sound assertion about a shared, per-user directory: "nothing new appeared" is a claim
    /// about the whole machine (the #489 defect), and "nothing of mine appeared" has no key.
    /// </para>
    /// <para>
    /// So it proves the same property at its source instead. The only way this row can write into
    /// that directory is if the disarm fails to hold AND the start fails; the assertion below
    /// interrogates the first conjunct directly, through the very decision
    /// <see cref="HeadlessTopology.StartAsync"/> makes, while the opt-out is set. A disarm that
    /// stopped working is caught whether or not the start happened to fail - which the file count
    /// could never do - and no other process can perturb the answer.
    /// </para>
    /// <para>
    /// <strong>A favourable trade, not a strict improvement, and the losing side is worth
    /// naming.</strong> The count observed the production path's EFFECT, so it would have caught
    /// drift AWAY from the factory - a <c>StartAsync</c> that stopped consulting
    /// <c>CreateUnlessDisabled</c> and constructed a recorder directly would have written a file
    /// and been seen, at the cost of needing the start to fail on the same run. The assertion
    /// below tests the factory in isolation and is blind to exactly that. It is preferred because
    /// what it gives up was conditional on a coincidence and what it gains is unconditional, not
    /// because it dominates.
    /// </para>
    /// <para>
    /// <strong>That residual is covered, and not here.</strong>
    /// <see cref="DcpRecorderFactoryCensusTests"/> pins the drift the count used to watch for -
    /// rule 1 refuses any construction of the recorder in production source outside the
    /// parameterless <c>CreateUnlessDisabled</c>, rule 2 refuses a <c>StartAsync</c> whose hand-off
    /// carries a recorder that did not come from it - structurally, in the blocking non-docker
    /// lane, on every run rather than on the runs where a start happens to fail. Being a
    /// source-level census it cannot see reflection, a target-typed <c>new()</c>, or a
    /// <c>global using</c> type alias declared in another file; those limits are enumerated in its
    /// own class remarks.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Register_InsideTheRealAspireHost_CapturesDcpTrafficAndNothingElse()
    {
        var original = Environment.GetEnvironmentVariable(DcpFlightRecorder.OptOutVariable);
        using var recorder = new DcpFlightRecorder();
        try
        {
            Environment.SetEnvironmentVariable(DcpFlightRecorder.OptOutVariable, "0");

            // The disarm holds: asked exactly as StartAsync asks it, with the environment in
            // exactly the state StartAsync will see, the production factory declines to create a
            // recorder - so no production recorder exists to write a capture, whatever the start
            // below does. `using` because a non-null answer is a disposable this row would
            // otherwise drop on the floor on its way to failing.
            using (var production = DcpFlightRecorder.CreateUnlessDisabled())
            {
                Assert.Null(production);
            }

            await using var topology = await HeadlessTopology.StartAsync(
                appHostAssemblyName: AppHostAssemblyName,
                configureResources: b => b.Services.AddLogging(
                    lb => DcpFlightRecorder.Register(lb, recorder)));

            Assert.NotNull(topology.Application);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DcpFlightRecorder.OptOutVariable, original);
        }

        var captured = recorder.Snapshot();

        // Guard against a vacuous pass first: a recorder that received nothing would satisfy
        // every "nothing unrelated was captured" assertion below for free.
        Assert.NotEmpty(captured);

        Assert.Contains(
            captured,
            e => e.Category.StartsWith(
                DcpFlightRecorder.DcpCategoryPrefix, StringComparison.OrdinalIgnoreCase));

        // The floor rule holds inside the host: no category outside Aspire reaches the recorder,
        // at any level.
        var strays = captured
            .Where(e => !e.Category.StartsWith(
                DcpFlightRecorder.AspireCategoryPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Category)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            "categories outside '" + DcpFlightRecorder.AspireCategoryPrefix
            + "' reached the recorder: " + string.Join(", ", strays));

        // And the Debug rule is the one that matters: below-Warning traffic arrives for DCP
        // categories, which is the evidence #420 has never captured.
        Assert.Contains(
            captured,
            e => e.Level < Microsoft.Extensions.Logging.LogLevel.Warning
                && e.Category.StartsWith(
                    DcpFlightRecorder.DcpCategoryPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The behavioural proof of the property the arming-window change exists for: a topology
    /// that STARTS cleanly and then never becomes READY writes a capture still holding the DCP
    /// traffic buffered before the health gate ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Assertion (e) is the one that matters.</strong> The capture written at the gate
    /// failure contains <c>Aspire.Hosting.Dcp*</c> lines, and those can only be there because the
    /// buffer survived <c>StartAsync</c> returning. An arming window that closed when the start
    /// returned — the shape this feature originally shipped — would leave the file with a header
    /// and nothing else.
    /// </para>
    /// <para>
    /// <strong>It took two attempts to make this row mean anything, and both failures are worth
    /// recording.</strong> First, the failing shape: <c>ports: [9093]</c> with a dead <c>tcp</c>
    /// check threw <c>Service &lt;name&gt; should have valid address at this point</c> out of
    /// <c>StartAsync</c> itself, so the flush that fired was the pre-existing start-path one and
    /// the row passed even with the widening reverted. That turned out not to be the fixture's
    /// fault at all: issue #420 was live on the host, and every port-publishing topology was
    /// failing to start. Once the host's DCP state store was repaired the same fixture produced
    /// a real gate failure (<c>Resource '...' failed to become healthy</c>). Second, the tail:
    /// this row originally required <c>dcp-tail:</c> in the detail and failed, because a clean
    /// start followed by a gate timeout logs nothing at Warning level — see the assertion block.
    /// </para>
    /// <para>
    /// <strong>The failing resource.</strong> <c>traefik/whoami</c> listens on 80 and only on 80.
    /// Declaring <c>httpPort: 8080</c> gives DCP a perfectly allocatable endpoint — so the start
    /// succeeds — behind which nothing serves, so the default HTTP health check on <c>/</c> never
    /// passes and the GATE is what fails. The container does not exit, so the gate does not
    /// short-circuit on <c>StopOnResourceUnavailable</c>; it times out at the
    /// <c>startupTimeout</c> passed below, which needed no new surface, public or internal.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task AFailingTopology_WritesACaptureIntoTheRedirectedDirectory()
    {
        var realDirectory = DcpCapture.ResolveDirectory();
        Assert.NotNull(realDirectory);

        var scratch = Path.Combine(
            Path.GetTempPath(), "vouchfx-dcp-gate-drill-" + Guid.NewGuid().ToString("N"));
        var originalOverride =
            Environment.GetEnvironmentVariable(DcpCapture.DirectoryOverrideVariable);

        try
        {
            // Redirect the PRODUCTION capture path, rather than reaching past it: this drives
            // HeadlessTopology's own flush, through SuiteTopology's own gate catch, exactly as a
            // real #420 would - it just lands somewhere this test owns.
            Environment.SetEnvironmentVariable(
                DcpCapture.DirectoryOverrideVariable, scratch);

            var environment = new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>
                {
                    // whoami listens on 80 and only on 80. Declaring httpPort 8080 gives DCP a
                    // perfectly allocatable endpoint - so the START succeeds - behind which
                    // nothing is serving, so the default HTTP health check on "/" never passes
                    // and the GATE is what fails. That split is the entire point of this row:
                    // an earlier version used `ports: [9093]` with a tcp check and, measured,
                    // threw inside app.StartAsync instead, which exercises the pre-existing
                    // start-path flush rather than the post-start window this drill exists for.
                    [_failingResourceName] = new ServiceSpec(
                        Image: "traefik/whoami",
                        Project: null,
                        ImagePullPolicy: null,
                        HttpPort: 8080,
                        Env: null),
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null);

            var failure = await Assert.ThrowsAsync<OrchestrationException>(
                async () => await SuiteTopology.StartAsync(
                    environment,
                    AppHostAssemblyName,
                    startupTimeout: TimeSpan.FromSeconds(8)));

            // (a) NOTHING OF THIS ROW'S OWN reached the operator's real directory, which is the
            //     honest form of "the redirect held". It cannot be "the real directory did not
            //     grow": that directory is per-user and shared with every other test host this
            //     user is running, so the count moves for reasons that have nothing to do with
            //     this row - which is #489, measured as expected 5 / actual 6 against a
            //     concurrent writer.
            //
            //     Property 1 is exact here. If the redirect were ignored, the production flush
            //     would put THIS topology's buffer - which names _failingResourceName, because
            //     DCP echoes the resource it is bringing up into the traffic the recorder holds,
            //     and assertion (c) below pins that on this very capture - into the real root.
            //     The name carries a per-run suffix, so nothing else on the machine, in this
            //     process or any other, runs a resource by it: a leak by this row run reddens
            //     this assertion and only this row run's leak can.
            //
            //     Checked BEFORE (b) deliberately. A neutralised redirect fails both, and this
            //     is the one whose message names the stray file: (b) would report "found 0 in
            //     the injected directory" and say nothing about the file now sitting in the
            //     operator's directory.
            var leaked = CapturesNaming(realDirectory!, _failingResourceName);
            if (leaked.Length > 0)
            {
                Assert.Fail(
                    "the redirect did not hold: " + leaked.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " capture(s) naming this run's own resource '" + _failingResourceName
                    + "' appeared in the operator's real capture directory. They are left in "
                    + "place deliberately - this row does not delete evidence - and must be "
                    + "removed by hand:\n  " + string.Join("\n  ", leaked.Select(Path.GetFileName)));
            }

            // (b) A capture appears in the INJECTED directory ...
            var captures = ListCaptures(scratch);
            if (captures.Length != 1)
            {
                Assert.Fail(
                    "expected exactly one capture in the injected directory, found "
                    + captures.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // (c) THE OWNERSHIP PREMISE ITSELF, pinned on a real capture in a blocking lane.
            //     Every leak assertion in this class - (a) above and the one in the healthy row -
            //     is an emptiness assertion over CapturesNaming, and emptiness assertions fail
            //     open: if DCP ever stopped echoing the resource name into the buffered traffic,
            //     the filter would return nothing for a row's OWN capture, both would pass for
            //     free, and no test would notice. This repo advances Aspire per engine release,
            //     so "what DCP echoes" is a moving dependency, not a constant.
            //
            //     THIRD, not last, and the position is load-bearing. This is the only positive
            //     check that the key exists at all, so every assertion placed ahead of it is one
            //     that can leave the premise unverified for a whole docker leg. It sits directly
            //     behind the two it cannot run without: (a), because a row that has already
            //     leaked should say so first, and (b), because it needs a capture to read.
            //
            //     Measured on real captures from this host, the key is genuine but thin and late:
            //     one 20-entry capture named its resource on entries 18 and 19 only, and this
            //     row's own drill capture held 18 entries with 2 naming the resource. Thin is
            //     survivable; silent is not.
            var body = await File.ReadAllTextAsync(captures[0]);
            Assert.Contains(_failingResourceName, body, StringComparison.OrdinalIgnoreCase);

            // (d) The failure carries the location TOKEN and the tail - and not the resolved
            //     path, which would put the operator's account name into a public CI artefact.
            Assert.Contains("dcp-capture: ", failure.Info.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(scratch, failure.Info.Detail, StringComparison.OrdinalIgnoreCase);

            // NOT asserted: "dcp-tail: ". The tail is Warning-and-above only, and a topology
            // that STARTS cleanly and then fails its health gate produces no warnings at all -
            // ports allocated fine, nothing complained. An earlier version of this row required
            // the tail and failed here, which is how the difference between the two failure
            // shapes was measured rather than assumed: the #420 shape warns, a plain gate
            // timeout does not.

            // ... and it names the OVERRIDE, not the default per-user root. Without this the
            // assertion above passed for the wrong reason: DescribeLocation used to ignore the
            // redirect entirely, so the scratch path was absent because the detail described a
            // location the file was never written to.
            Assert.Contains(
                DcpCapture.DirectoryOverrideVariable,
                failure.Info.Detail,
                StringComparison.Ordinal);

            // (e) The capture holds DCP traffic rather than being an empty file with a header.
            //     See this row's remarks for what that does and does NOT establish about WHICH
            //     of the two flush sites produced it.
            //
            //     Composed on the failing path only, which here is more than tidiness: the
            //     description below walks the whole body, so Assert.True would have built it on
            //     every green run.
            //
            //     WHAT THE MESSAGE CARRIES AND WHAT IT REFUSES TO (#526). It carries the bare
            //     file name, the byte count, the header and entry line counts, and a capped
            //     census of the logger CATEGORIES the entries arrived under. It carries no OTHER
            //     part of the body - no message segment, no exception segment, no header line's
            //     content - and no path, neither the scratch directory's nor the file's. The body
            //     is the buffered traffic of a real Aspire host, so it can hold per-run generated
            //     passwords, values this suite declared through `env`, and absolute host paths;
            //     this message reaches a public CI job log.
            //
            //     Each field earns its place separately. The file name is DcpCapture.BuildFileName
            //     output and nothing else - a fixed prefix, a UTC stamp off the clock, the
            //     suffix, plus a numeric collision suffix - so it is composed entirely from
            //     things the ENGINE chose. The three counts are integers. The categories are the
            //     one field drawn from the file, and DescribeCaptureForDiagnostics' remarks argue
            //     that one at length.
            //
            //     The census is also the more useful diagnostic here, not merely the safer one:
            //     this branch fires exactly when NO line names DcpCategoryPrefix, so the matching
            //     lines are empty BY CONSTRUCTION and the question worth answering is which
            //     categories did arrive. Three answers, and the counts separate them: none at all
            //     (an empty buffer - zero entry lines); Aspire's non-DCP categories only (either
            //     the routing or the filter rules regressed, or the arming window closed before
            //     DCP logged anything - those two look alike here and the census cannot tell them
            //     apart); or DCP traffic that WAS recorded and was then evicted by later non-DCP
            //     Aspire traffic under the recorder's bounds, which leaves entry lines but no DCP
            //     line and announces itself in the header count - FormatCapture adds a TRUNCATED
            //     note when anything was evicted, making the header five lines rather than four.
            //
            //     Which is why the message below states the OBSERVATION and stops. It used to
            //     diagnose ("the buffer was empty - the arming window closed too early"), which
            //     named one of the three and was then immediately contradicted by the census
            //     printed beside it: a drill produced "17 entry line(s)" under that sentence.
            var dcpLines = body
                .Split('\n')
                .Where(l => l.Contains(
                    DcpFlightRecorder.DcpCategoryPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (dcpLines.Count == 0)
            {
                Assert.Fail(
                    "the capture written at the health-gate timeout contains no "
                    + DcpFlightRecorder.DcpCategoryPrefix
                    + "* line by the time the gate failed. "
                    + DescribeCaptureForDiagnostics(captures[0], body)
                    + " The body is deliberately not reproduced here (#526). Nor is it kept: this "
                    + "row redirects the capture to a scratch directory of its own - overriding "
                    + "any " + DcpCapture.DirectoryOverrideVariable + " the operator set - and "
                    + "attempts to delete it as it unwinds, so reading a full body means running "
                    + "the row with that deletion suspended.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DcpCapture.DirectoryOverrideVariable, originalOverride);

            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// <see cref="CapturesNaming"/> reports a capture whose body names the calling row run's
    /// token even when that file was ALREADY in the directory, and reports nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this row pins, and what it cannot.</strong> It writes a file naming this run's
    /// token BEFORE calling the filter and requires the filter to report it, so a stage keyed on
    /// age relative to the CALL — newer than a mark taken on entry, say — would drop that file
    /// and redden this row. A stage keyed on the ROW's start would not: the snapshot #538 removed
    /// was one, and these files are written after the row begins, so such a stage would accept
    /// them and this row would stay green. The removal therefore rests on the redundancy argument
    /// in <see cref="CapturesNaming"/>'s remarks, not on this row; what this row guards is the
    /// contract left behind — a file naming the token is this run's, whatever its age.
    /// </para>
    /// <para>
    /// <strong>The arrangement is synthesised, and saying so is the point.</strong> A live row
    /// cannot produce it: the token is minted when the row's instance is built, so a capture that
    /// was on disk before that construction cannot name it. That is the same fact that makes the
    /// snapshot stage redundant, and <see cref="CapturesNaming"/>'s remarks record why the reuse
    /// story #538 was filed on does not hold against today's engine. A contract worth keeping is
    /// still worth pinning, and it can only be pinned by hand.
    /// </para>
    /// <para>
    /// Deterministic and docker-free deliberately. The property is a claim about the FILTER, not
    /// about a topology, so it belongs in the blocking non-docker lane where a regression cannot
    /// wait for a docker leg to be noticed. The row owns its directory — a Guid-suffixed one under
    /// <see cref="Path.GetTempPath"/>, removed in a <c>finally</c> — so nothing here reads, writes
    /// or deletes in the operator's real capture directory.
    /// </para>
    /// <para>
    /// <strong>What each of the three files pins, since only two of them discriminate.</strong>
    /// The naming file is the answer; the foreign one proves the filter is not simply returning
    /// everything it enumerates, and it carries this same instance's OTHER token, so "does not
    /// contain" holds by the differing prefix rather than by a Guid not colliding. The zero-length
    /// file discriminates nothing — an empty body cannot contain any token, so removing the
    /// filter's own length floor would leave this row green — and pins the weaker property that
    /// is still worth having: a zero-length regular file is neither reported nor able to throw out
    /// of a method one caller runs inside a <c>finally</c>. That file shape is what another
    /// process's mid-write leaves on disk.
    /// </para>
    /// </remarks>
    [Fact]
    public void CapturesNaming_ReportsAPreExistingFileThatNamesTheToken()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "vouchfx-dcp-naming-drill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // Minted through production's own BuildFileName so the three names stay coupled to
            // the layout production writes. A literal copied here would fail SAFE on a prefix or
            // suffix change (ListCaptures builds its glob from the same constants, so the listing
            // would come back empty and this row red), but a change to the timestamp layout inside
            // an unchanged glob would leave the literal enumerated and this row green while
            // production wrote a shape it no longer exercised. Distinct milliseconds, because that
            // is the layout's resolution and two files may not share a name.
            var stamp = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
            var naming = Path.Combine(directory, DcpCapture.BuildFileName(stamp));
            var foreign = Path.Combine(
                directory, DcpCapture.BuildFileName(stamp.AddMilliseconds(1)));
            var empty = Path.Combine(
                directory, DcpCapture.BuildFileName(stamp.AddMilliseconds(2)));

            // A capture body is a header plus DCP log lines; all the filter asks of it is whether
            // the resource name occurs, so a representative line is enough.
            File.WriteAllText(
                naming, "Aspire.Hosting.Dcp: creating container " + _healthyResourceName + "\n");
            File.WriteAllText(
                foreign, "Aspire.Hosting.Dcp: creating container " + _failingResourceName + "\n");
            File.WriteAllText(empty, string.Empty);

            // File names rather than full paths: which of the three files came back is the
            // property, and the path spelling EnumerateFiles happens to return is not.
            Assert.Equal(
                new[] { Path.GetFileName(naming) },
                CapturesNaming(directory, _healthyResourceName)
                    .Select(Path.GetFileName)
                    .ToArray());

            Assert.Equal(
                new[] { Path.GetFileName(foreign) },
                CapturesNaming(directory, _failingResourceName)
                    .Select(Path.GetFileName)
                    .ToArray());
        }
        finally
        {
            // Guarded for the same reason the failing row's scratch cleanup is: a throw here
            // would replace the assertion failure that is the row's whole output.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The captures in <paramref name="directory"/> whose body names
    /// <paramref name="resourceName"/> — that is, the ones the calling row can prove are its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ownership key is the resource name the calling row splices into its topology, carrying
    /// that row run's own Guid suffix. A capture body is <c>DcpFlightRecorder.FormatCapture</c>'s
    /// output: a header plus the buffered DCP log lines, and DCP names the resource it is
    /// creating, waiting on or failing in those lines. So a capture produced by the calling row
    /// run's topology contains that name and no other capture on the machine does — including
    /// one written by a concurrent host running this same row.
    /// </para>
    /// <para>
    /// <strong>That name is the WHOLE ownership test, and the per-run suffix is what makes it
    /// sufficient on its own.</strong> The token is minted per row execution
    /// (see <see cref="_healthyResourceName"/>), so no capture written by any other run — earlier,
    /// later or concurrent, in this process or another — can contain it. A file that was already
    /// sitting in the directory when the row began therefore IS this row run's if it names the
    /// token, and is not if it does not. WHEN a file appeared carries no information the token
    /// does not already carry.
    /// </para>
    /// <para>
    /// <strong>Which is why the pre-run snapshot this method used to difference against is gone —
    /// #538.</strong> Against an instance key the stage adds no exclusion power: everything it
    /// could exclude, the key already rejects, so all it can still do is DISCARD a file the key
    /// accepts.
    /// That is the whole case for removing it, and it needs no failure mode to stand.
    /// </para>
    /// <para>
    /// <strong>The failure mode #538 was filed on is NOT reachable against today's engine, and
    /// saying so is cheaper than carrying a story that does not hold.</strong> The hypothesis was
    /// that a snapshotted name could be freed by another writer's retention pass and then taken by
    /// this row's own capture, so the difference would drop the row's own file by pathname. It
    /// cannot: a capture name encodes the moment it was written
    /// (<see cref="DcpCapture.BuildFileName(DateTimeOffset)"/>, from <c>DateTimeOffset.UtcNow</c>
    /// at the flush), so every name in a snapshot taken at T0 encodes a moment at or before T0
    /// while this row's capture is stamped later and can equal none of them; and a name already
    /// taken on disk is not reusable anyway, because <c>DcpCapture</c> creates the file with
    /// <c>FileMode.CreateNew</c> and retries under a <c>-N</c> suffix on collision. The chain
    /// closes only under a host clock that stepped backwards — the case
    /// <c>DcpCapture.SelectForDeletion</c>'s own <c>justWritten</c> remark already names.
    /// <see cref="CapturesNaming_ReportsAPreExistingFileThatNamesTheToken"/> pins what is left
    /// of the contract, deterministically and without Docker.
    /// </para>
    /// <para>
    /// <strong>WHICH directory this is, since this method both reads and — in one caller —
    /// deletes.</strong> Not necessarily a 0700 per-user root:
    /// <see cref="DcpCapture.ResolveDirectory()"/> lets <c>VOUCHFX_DCP_CAPTURE_DIR</c> outrank
    /// <c>LocalApplicationData</c>, and this project's own troubleshooting guide recommends
    /// pointing it at <c>${{ github.workspace }}/vouchfx-captures</c>. An operator following that
    /// recipe has this method enumerating, reading and deleting inside a workspace. The blast
    /// radius stays bounded by the ownership key and the <c>dcp-capture-*.log</c> glob — which on
    /// Windows admits a little more than it reads as, since a three-character extension pattern
    /// there also matches extensions that merely BEGIN with it (<c>.logs</c>, <c>.logfile</c>) —
    /// but it is not confined to a private directory, so it is stated rather than assumed away.
    /// </para>
    /// <para>
    /// <strong>A racing writer must not be able to throw out of the per-file READ.</strong> Two
    /// things can happen between the listing and the read: another process's capture is mid-write,
    /// and <c>DcpCapture</c>'s own retention (<c>RetainedFiles</c>) deletes a listed file to make
    /// room. Both surface as <see cref="IOException"/> — <see cref="FileNotFoundException"/> is
    /// one, so it needs no separate clause — or, on a locked file, as
    /// <see cref="UnauthorizedAccessException"/>. An unreadable file is treated as NOT MINE.
    /// </para>
    /// <para>
    /// That is the safe direction rather than the lenient one, though not an airtight one, and the
    /// two ways it can be wrong are both narrow. Misclassifying a FOREIGN file as mine would
    /// redden the calling row for someone else's work and, in the row that deletes, destroy a
    /// capture it does not own — the ownership key exists to prevent exactly that. Misclassifying
    /// MY OWN file as foreign needs the row's own capture to be unreadable, which is reachable
    /// rather than impossible: <c>DcpCapture.WriteAsync</c> abandons a stalled write after five
    /// seconds without cancelling the I/O, and the file it left behind can still be open under
    /// <c>FileShare.None</c> when this read arrives. The row's own leak would then escape both
    /// the assertion and the deletion. Accepted: the alternative — treating unreadable as mine —
    /// turns every foreign mid-write into a red row and a destroyed file.
    /// </para>
    /// <para>
    /// <strong>The listing is NOT covered by that.</strong> <see cref="ListCaptures"/> does
    /// <c>Directory.Exists</c> and then enumerates, which is TOCTOU-prone, and a directory
    /// removed or made unreadable in between throws out of this method. That is deliberate: in
    /// the failing-topology row a throw is an honest failure, and in the healthy row the caller
    /// catches it around its whole cleanup precisely so it cannot displace the topology exception
    /// — see that <c>finally</c>.
    /// </para>
    /// </remarks>
    private static string[] CapturesNaming(string directory, string resourceName)
    {
        // A capture cannot be larger than the recorder's own budget plus its header, so anything
        // past this bound is not one of ours and is not worth reading. The bound is not thrift:
        // File.ReadAllText is unbounded and uncancellable and this runs inside a finally, and
        // EnumerateFiles will happily return a FIFO on Unix - reading one blocks for ever, and
        // DcpCapture.CreateDirectoryOwnerOnly deliberately does not narrow the mode of a
        // PRE-EXISTING directory, so a group-writable capture directory is possible. A FIFO also
        // reports length 0, which the lower bound rejects; a zero-length regular file is a
        // mid-write or a stub and is not a capture either.
        //
        // The bound is per FILE. Since #538 the listing is no longer differenced against a
        // snapshot, so every dcp-capture-*.log in the directory is considered - stat'd, and read
        // when it clears the bound: a count bounded by
        // DcpCapture.RetainedFiles where the engine wrote them, and by nothing at all in an
        // operator-chosen directory.
        const int HeaderSlack = 8 * 1024;
        var maxCaptureBytes = (long)DcpFlightRecorder.DefaultCharLimit * 4 + HeaderSlack;

        return ListCaptures(directory)
            .Where(path => Names(path, resourceName, maxCaptureBytes))
            .ToArray();

        static bool Names(string capturePath, string token, long maxBytes)
        {
            try
            {
                var length = new FileInfo(capturePath).Length;
                if (length <= 0 || length > maxBytes)
                {
                    return false;
                }

                return File.ReadAllText(capturePath)
                    .Contains(token, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                // Mid-write by another process, or pruned by retention between the listing and
                // this read. Not mine - see the remarks for why that is the safe answer.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// <see cref="DescribeCaptureForDiagnostics"/> counts a real capture exactly, names only its
    /// categories, and lets no message text or directory segment through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this row the helper had no test at all.</strong> It runs on one branch of
    /// one Docker row, and that branch fires only when something has already regressed — so a
    /// green 4/4 says nothing about it, and the first version's off-by-one line count (it counted
    /// the empty element <c>Split</c> leaves after the body's final newline, reporting 24 lines
    /// for a 23-line file) shipped through exactly that gap. What the helper promises about a
    /// WELL-FORMED capture is asserted here instead, in the blocking non-docker lane: the counts,
    /// the byte figure, the file name, the refusal of message text and directory segments, the
    /// cap and its tail, and the 64-character truncation with its marker.
    /// </para>
    /// <para>
    /// <strong>ONE degenerate branch remains unasserted, and it is named rather than implied.
    /// </strong> The separator-absent fallback cannot be reached from a real capture:
    /// <c>FormatCapture</c> always writes its <c>----</c> line, so a body without one is a file
    /// this helper's caller could never have read. That branch is a guard against a malformed
    /// file, not a contract any row can state without hand-writing a fixture and giving up the
    /// property both rows are built on — that the input came from the production formatter.
    /// </para>
    /// <para>
    /// The other degenerate shapes are reachable, and an earlier version of this paragraph wrongly
    /// filed two of them here as unproducible. All are pinned from real formatter output: the
    /// empty-buffer shape — zero entries, and the "none - the capture holds no entry line"
    /// wording — by
    /// <see cref="DescribeCaptureForDiagnostics_WithNoEntries_ReportsHeaderSizeAndNoCategory"/>;
    /// the entries-but-no-category shape — entry lines present, none yielding a category, and the
    /// "none - no entry line yielded a category" wording — by
    /// <see cref="DescribeCaptureForDiagnostics_WithUncategorisedEntries_CountsThemAndNamesNone"/>;
    /// and <c>CategoryOf</c>'s empty-category guard by the <c>CreateLogger("")</c> entry in this
    /// row's own fixture, which the recorder renders as a genuine
    /// <c>{stamp} {level} : {message}</c> line because nothing between <c>CreateLogger</c> and
    /// the sanitiser rejects an empty name.
    /// </para>
    /// <para>
    /// <strong>The body is built by the production formatter, not typed out here.</strong>
    /// <c>DcpFlightRecorder.FormatCapture</c> writes the header block, the <c>----</c> separator
    /// and the entry lines this helper parses; a hand-written fixture would pin this row to a
    /// layout the engine had stopped emitting. The recorder is driven through
    /// <c>CreateLogger</c>/<c>DcpTestLog.Emit</c>, which is how the host fills it.
    /// </para>
    /// <para>
    /// <strong>The arrangement is chosen to exercise every branch that can leak.</strong> Twelve
    /// distinct categories against a cap of eight, so the cap and the "+N more" tail both fire; a
    /// pair of categories longer than the 64-character cut and sharing a 67-character prefix, so
    /// the truncation, its ASCII marker and the documented lossy case (rendered identically,
    /// counted separately) are all observed; one category carrying <c>": "</c>, so the parse's
    /// AT-MOST property is observed rather than assumed; one entry whose MESSAGE holds a
    /// path-and-secret sentinel, asserted present in the body and absent from the description, so
    /// the refusal is measured on a string that really was there; and a two-segment directory in
    /// the path, so "bare file name" is a claim about this row's input rather than about a
    /// filename that never had a directory.
    /// </para>
    /// </remarks>
    [Fact]
    public void DescribeCaptureForDiagnostics_CountsTheCaptureAndNamesOnlyItsCategories()
    {
        // TWELVE distinct categories in first-seen order: the first eight are what the census may
        // show, the last four are what "+4 more" must stand for.
        //
        // Two of the eight are the long pair. They share a 67-character prefix - longer than the
        // helper's 64-character cut - and differ only after it, so each must render as the SAME
        // truncated token while still being COUNTED as two. That is the one lossy direction the
        // helper's remarks admit to, and it is asserted here rather than left as prose.
        //
        // The sixth carries a ": " of its own, so the parse must yield its prefix and stop there.
        const string LongShared =
            "Aspire.Hosting.Dcp.VeryLongCategoryNameSharingAPrefixWithItsSibling";
        const string LongOne = LongShared + "One";
        const string LongTwo = LongShared + "Two";
        const string AwkwardCategory = "Aspire.Hosting.Dcp.awkward: colon";
        const string AwkwardPrefix = "Aspire.Hosting.Dcp.awkward";
        const string CappedOne = "Aspire.Hosting.Dcp.CappedOne";
        const string CappedTwo = "Aspire.Hosting.Dcp.CappedTwo";
        var shown = new[]
        {
            "Aspire.Hosting.Dcp.DcpExecutor",
            "Aspire.Hosting.Dcp.KubernetesService",
            LongOne,
            LongTwo,
            "Aspire.Hosting.Lifecycle",
            AwkwardCategory,
            "Aspire.Hosting.Dcp.DcpHost",
            "Aspire.Hosting.Dcp.NetworkReconciler",
        };
        var hidden = new[]
        {
            "Aspire.Hosting.Dcp.dcp.start-apiserver.api-server",
            "Aspire.Hosting.ApplicationModel",
            CappedOne,
            CappedTwo,
        };

        // The premise the long pair rests on, asserted rather than counted by hand: the shared
        // part really does outrun the cut, so the two really are indistinguishable after it.
        Assert.True(LongShared.Length > 64);

        // A path and a credential in ONE message, which is the disclosure shape #526 is about.
        const string Sentinel = @"C:\secret\hunter2";

        using var recorder = new DcpFlightRecorder();

        // FIRST entry, under an EMPTY category name, and first on purpose. Nothing between
        // CreateLogger and the sanitiser rejects one - RecordingLogger stores
        // `category ?? string.Empty` and ToCappedPrintableAsciiLine returns empty for empty - so
        // the recorder renders a real "{stamp} {level} : {message}" line. It is an entry, so it
        // counts toward the entry total, and CategoryOf must refuse it rather than census a blank
        // token. Emitted ahead of every named category so that, without the guard, the blank
        // token would take the FIRST shown slot and the assertions below would fire; emitted
        // last, it would have landed beyond the cap and the same assertions could not fail.
        DcpTestLog.Emit(
            recorder.CreateLogger(string.Empty),
            Microsoft.Extensions.Logging.LogLevel.Debug,
            "reconciling resource");

        foreach (var category in shown.Concat(hidden))
        {
            DcpTestLog.Emit(
                recorder.CreateLogger(category),
                Microsoft.Extensions.Logging.LogLevel.Debug,
                "reconciling resource");
        }

        // Fourteenth entry, repeating the first named category: the census must count DISTINCT
        // tokens, so this must not consume one of the eight slots.
        DcpTestLog.Emit(
            recorder.CreateLogger(shown[0]),
            Microsoft.Extensions.Logging.LogLevel.Warning,
            "stopping container; credential file " + Sentinel + " left on disk");

        var body = recorder.FormatCapture(
            new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));

        // Non-vacuity, three ways: the sentinel really is in the body, the body really does carry
        // the entry count this row is about to require of the description, and the empty-category
        // line really was rendered rather than dropped on the way in.
        Assert.Contains(Sentinel, body, StringComparison.Ordinal);
        Assert.Contains("entries: 14, evicted: 0", body, StringComparison.Ordinal);
        Assert.Contains(" : reconciling resource", body, StringComparison.Ordinal);

        var description = DescribeCaptureForDiagnostics(
            Path.Combine("unlikely-parent-segment", "unlikely-child-segment", "dcp-capture-x.log"),
            body);

        // The counts. Four header lines is FormatCapture's layout with nothing evicted (banner,
        // written, issue, entries); a TRUNCATED note would make it five, which is the eviction
        // signature assertion (e)'s comment names.
        Assert.Contains(
            body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " byte(s)",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "4 header line(s), 14 entry line(s)", description, StringComparison.Ordinal);

        // The file name, and neither directory segment.
        Assert.Contains("dcp-capture-x.log", description, StringComparison.Ordinal);
        Assert.DoesNotContain("unlikely-parent-segment", description, StringComparison.Ordinal);
        Assert.DoesNotContain("unlikely-child-segment", description, StringComparison.Ordinal);

        // No message text, and the message text that must not appear is the one just proved to be
        // in the body. The message segment shared by thirteen entries is checked too, so the
        // refusal is not merely about the unusual-looking string.
        Assert.DoesNotContain(Sentinel, description, StringComparison.Ordinal);
        Assert.DoesNotContain("reconciling resource", description, StringComparison.Ordinal);
        Assert.DoesNotContain("stopping container", description, StringComparison.Ordinal);

        // The five of the eight that render verbatim. The awkward one and the long pair do not,
        // and each is checked on its own terms below.
        foreach (var category in shown.Where(
            c => c != AwkwardCategory && c != LongOne && c != LongTwo))
        {
            Assert.Contains(category, description, StringComparison.Ordinal);
        }

        // The awkward one renders as its prefix and no further: its own ": " ends the slice,
        // which is why the slice can never reach a message.
        Assert.Contains(AwkwardPrefix, description, StringComparison.Ordinal);
        Assert.DoesNotContain(AwkwardCategory, description, StringComparison.Ordinal);

        // The long pair. Neither survives whole - which is the 64-character cut doing its work -
        // and both collapse onto ONE rendered token carrying the ASCII marker. The token is
        // therefore expected TWICE: rendered identically, counted separately. That doubled token
        // is also the bound in miniature, 64 characters plus a three-character marker, which is
        // what makes the census's worst case 8 x 67 characters of category text.
        var truncated = string.Concat(LongShared.AsSpan(0, 64), "...");
        Assert.DoesNotContain(LongOne, description, StringComparison.Ordinal);
        Assert.DoesNotContain(LongTwo, description, StringComparison.Ordinal);
        Assert.Equal(67, truncated.Length);
        Assert.Equal(2, description.Split(truncated).Length - 1);

        // Four distinct categories never reached the census, and the tail says so without naming
        // them - including the long pair's two slots, which counted separately even though they
        // rendered as one token.
        Assert.Contains("+4 more", description, StringComparison.Ordinal);
        foreach (var category in hidden)
        {
            Assert.DoesNotContain(category, description, StringComparison.Ordinal);
        }

        // The empty-category entry censused as NOTHING rather than as a blank token. It was the
        // first entry emitted, so without the guard the blank would be the first shown item and
        // "present: ," would appear; it also did not enter `seen`, which is why "+4 more" above
        // is four and not five - the guard refuses the line outright rather than counting an
        // unnameable category toward the hidden tail.
        Assert.DoesNotContain("present: ,", description, StringComparison.Ordinal);
        Assert.DoesNotContain(", ,", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture whose buffer was empty describes as a real file with zero entries, and names no
    /// category at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The empty-buffer shape is producible, which is why it is pinned here rather than
    /// listed as unreachable.</strong> <c>FormatCapture</c> on a recorder that never recorded
    /// anything still writes its four header lines and its <c>----</c> separator, so the helper
    /// meets a body with a separator, no entry lines, and nothing to census. That drives two
    /// branches nothing else reaches: <c>entryLines</c> resolving to zero, and the
    /// "none - the capture holds no entry line" wording.
    /// </para>
    /// <para>
    /// <strong>An empty BUFFER is not an empty FILE, and the byte figure is what says so.</strong>
    /// The header is real text, so the description must report the header's own size rather than
    /// zero — an operator reading "0 byte(s)" would be looking for a truncated write, which is a
    /// different fault entirely. The assertion is written against <c>body.Length</c> rather than
    /// against the literal absence of "0 byte(s)": any length ending in a zero digit contains
    /// that text, so a literal-absence assertion would fail on a capture whose header happened to
    /// be 250 bytes long. The property is the figure, not its spelling.
    /// </para>
    /// </remarks>
    [Fact]
    public void DescribeCaptureForDiagnostics_WithNoEntries_ReportsHeaderSizeAndNoCategory()
    {
        using var recorder = new DcpFlightRecorder();

        var body = recorder.FormatCapture(
            new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));

        // Non-vacuity: the formatter really did produce an entry-less body, and it is not empty.
        Assert.Contains("entries: 0, evicted: 0", body, StringComparison.Ordinal);
        Assert.True(body.Length > 0);

        var description = DescribeCaptureForDiagnostics(
            Path.Combine("unlikely-parent-segment", "unlikely-child-segment", "dcp-capture-x.log"),
            body);

        Assert.Contains(
            body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " byte(s)",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "4 header line(s), 0 entry line(s)", description, StringComparison.Ordinal);
        Assert.Contains(
            "Categories present: none - the capture holds no entry line.",
            description,
            StringComparison.Ordinal);

        // The same disclosure floor as the populated row: bare name, neither directory segment.
        Assert.Contains("dcp-capture-x.log", description, StringComparison.Ordinal);
        Assert.DoesNotContain("unlikely-parent-segment", description, StringComparison.Ordinal);
        Assert.DoesNotContain("unlikely-child-segment", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture whose every entry line yields no category describes as a file WITH entries and
    /// says so, rather than claiming the buffer was empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "none" wording is keyed on the entry count precisely so that it cannot contradict the
    /// count printed beside it; this row is the branch where the two would otherwise have
    /// disagreed — entry lines present, none of them censused. It is reachable from real
    /// formatter output because <c>CreateLogger</c> accepts an empty category name and the
    /// recorder renders it as a genuine <c>{stamp} {level} : {message}</c> line, which
    /// <c>CategoryOf</c>'s empty-category guard refuses. That guard is pinned negatively by the
    /// populated row (no blank token in the census) and positively here (the guard is the ONLY
    /// thing that empties this census).
    /// </para>
    /// </remarks>
    [Fact]
    public void DescribeCaptureForDiagnostics_WithUncategorisedEntries_CountsThemAndNamesNone()
    {
        using var recorder = new DcpFlightRecorder();
        DcpTestLog.Emit(
            recorder.CreateLogger(string.Empty),
            Microsoft.Extensions.Logging.LogLevel.Debug,
            "reconciling resource");

        var body = recorder.FormatCapture(
            new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));

        // Non-vacuity: the formatter really did record one entry, under an empty category.
        Assert.Contains("entries: 1, evicted: 0", body, StringComparison.Ordinal);
        Assert.Contains(" : reconciling resource", body, StringComparison.Ordinal);

        var description = DescribeCaptureForDiagnostics("dcp-capture-x.log", body);

        Assert.Contains(
            "4 header line(s), 1 entry line(s)", description, StringComparison.Ordinal);
        Assert.Contains(
            "Categories present: none - no entry line yielded a category.",
            description,
            StringComparison.Ordinal);
        Assert.DoesNotContain("holds no entry line", description, StringComparison.Ordinal);
        Assert.DoesNotContain("reconciling resource", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bounded, non-disclosing description of a capture file for a failure message: its bare
    /// name, its byte count, its header and entry line counts, and the DISTINCT logger categories
    /// its entries arrived under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What it refuses to return is the reason it exists — #526.</strong> No part of the
    /// body except the categories: not a MESSAGE segment, not an EXCEPTION segment, not a header
    /// line's content, and not a path. A capture is the buffered traffic of a real Aspire host, so
    /// it can hold per-run generated passwords for managed dependencies, values the suite declared
    /// through <c>env</c>, and absolute host paths — and the message this feeds reaches a public
    /// CI job log. The caller that used to paste the whole body in is the defect being closed.
    /// </para>
    /// <para>
    /// <strong>Why a CATEGORY may be printed where a message may not, stated against the pinned
    /// Aspire rather than in general.</strong> Only <c>Aspire</c>-prefixed categories can be in a
    /// capture at all: <see cref="DcpFlightRecorder.Register"/> installs
    /// <c>AddFilter&lt;DcpFlightRecorder&gt;(category: null, LogLevel.None)</c> as the floor and
    /// then re-admits exactly the <c>Aspire</c> and DCP prefixes. The type argument is
    /// load-bearing, not decoration: it scopes every one of those rules to THIS provider, so the
    /// floor silences the recorder alone and leaves the console's own levels untouched. Nothing
    /// outside the re-admitted prefixes ever reaches the buffer.
    /// </para>
    /// <para>
    /// Those names ARE composed at run time, and the honest claim is narrower than "they are
    /// static". Measured on Aspire 13.4.2, a real gate-failure capture from this row yielded
    /// <c>Aspire.Hosting.Dcp.DcpExecutor</c> and <c>Aspire.Hosting.Dcp.KubernetesService</c>
    /// alongside <c>Aspire.Hosting.Dcp.dcp.start-apiserver.api-server</c> — the second shape is
    /// built at run time from DCP's own component and process names. What none of them is built
    /// from is LOGGED CONTENT: the composed parts are names the engine, DCP or the topology
    /// chose — and the topology's names, in this suite, are ones this row itself chose. That is
    /// the property the message segment lacks, and it is what makes a category printable where a
    /// message is not.
    /// </para>
    /// <para>
    /// This repo advances Aspire per engine release, so the paragraph above is a statement about
    /// a pinned dependency and not a law. The cap of eight and the per-token truncation at 64
    /// characters are the standing defence if a later Aspire composes a category out of something
    /// observed: they bound the category text to 8 x 67 characters whatever the file holds. The
    /// truncation is lossy in one direction worth knowing about — two categories sharing a
    /// 64-character prefix render identically here while still being counted as two.
    /// </para>
    /// <para>
    /// <strong>The parse is <see cref="DcpFlightRecorder"/>'s own line layout</strong> —
    /// <c>{stamp} {level} {category}: {message}</c>, with a header block ahead of the entries that
    /// <c>FormatCapture</c> closes with a <c>----</c> line. Neither the stamp nor the
    /// four-character level token contains a space, so what sits between the second space and the
    /// first <c>": "</c> is AT MOST the category: a category containing <c>": "</c> itself yields
    /// a prefix of it, never more. That inequality is the safety property — the slice stops at the
    /// first <c>": "</c>, so it cannot reach into the message however the category is spelled. A
    /// line that does not parse is skipped rather than guessed at: this runs while the row is
    /// already failing, and a wrong token in a diagnostic is worse than a missing one.
    /// </para>
    /// </remarks>
    private static string DescribeCaptureForDiagnostics(string path, string body)
    {
        const int MaxCategories = 8;
        const int MaxCategoryChars = 64;

        var lines = body.Split('\n');

        // FormatCapture TERMINATES every line it writes - the header lines, the separator and
        // each entry - so the split always yields one trailing empty element that is not a line.
        // Exactly one is dropped, and never by TrimEnd: a greedy trim would also eat a genuinely
        // empty last line and under-report the count, which is the class Copilot caught on #528.
        var lineCount = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        // Everything ahead of FormatCapture's "----" is header: counted, never quoted. No
        // separator at all means nothing here is a recognisable entry line.
        var separator = Array.IndexOf(lines, "----");
        var headerLines = separator < 0 ? lineCount : separator;
        var entryLines = separator < 0 ? 0 : lineCount - (separator + 1);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shown = new List<string>();
        for (var i = headerLines + 1; i < lineCount; i++)
        {
            var category = CategoryOf(lines[i]);
            if (category is null || !seen.Add(category))
            {
                continue;
            }

            if (shown.Count < MaxCategories)
            {
                shown.Add(
                    category.Length <= MaxCategoryChars
                        ? category
                        : string.Concat(category.AsSpan(0, MaxCategoryChars), "..."));
            }
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var hidden = seen.Count - shown.Count;

        // body.Length IS the byte count, not an approximation of it, and reading it off the
        // string rather than off the file is what keeps this composition unable to throw.
        // DcpFlightEntry.Create folds every component to printable ASCII precisely so one
        // character is one byte, and DcpCapture writes UTF8Encoding(false) - no BOM - for the
        // same premise. A FileInfo probe here would be an unguarded I/O call inside a failure
        // message: its exception would replace the assertion, and its own message carries the
        // FULL path, which is the disclosure this method exists to prevent.
        return "The capture is '" + Path.GetFileName(path) + "', "
            + body.Length.ToString(invariant) + " byte(s), "
            + headerLines.ToString(invariant) + " header line(s), "
            + entryLines.ToString(invariant) + " entry line(s). Categories present: "
            // Keyed on the ENTRY count, not on the census: every entry line can fail to yield a
            // category (an empty category name does exactly that), and "no line parsed" beside
            // "14 entry line(s)" would be the message contradicting itself again.
            + (shown.Count == 0
                ? (entryLines == 0
                    ? "none - the capture holds no entry line."
                    : "none - no entry line yielded a category.")
                : string.Join(", ", shown)
                    + (hidden > 0
                        ? ", +" + hidden.ToString(invariant) + " more."
                        : "."));

        static string? CategoryOf(string line)
        {
            var afterStamp = line.IndexOf(' ');
            if (afterStamp < 0)
            {
                return null;
            }

            var afterLevel = line.IndexOf(' ', afterStamp + 1);
            if (afterLevel < 0)
            {
                return null;
            }

            // colon == afterLevel + 1 is an EMPTY category ("{stamp} {level} : {message}"), which
            // would otherwise census as a stray empty token. Not a line this method can describe.
            var colon = line.IndexOf(": ", afterLevel + 1, StringComparison.Ordinal);
            return colon <= afterLevel + 1 ? null : line[(afterLevel + 1)..colon];
        }
    }

    private static string[] ListCaptures(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(
                directory, DcpCapture.FileNamePrefix + "*" + DcpCapture.FileNameSuffix)
            .ToArray();
    }
}
