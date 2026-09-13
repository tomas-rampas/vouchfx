using Microsoft.Extensions.DependencyInjection;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// The three assertions the #420 flight recorder needs a real topology for: that a start which
/// SUCCEEDS leaves nothing behind, that the filter rules survive contact with the real Aspire
/// host, and that a FAILING topology writes a capture holding real DCP traffic through the
/// production flush path.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about the recorder is pinned by fast drills against injected seams, because
/// the fault it captures is not reproducible on demand. These three cannot be: each is a claim
/// about the production wiring inside a running Aspire host.
/// </para>
/// <para>
/// <strong>Each row states its own relationship to the operator's REAL capture directory,
/// because they differ and the difference is deliberate:</strong>
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
    /// captures carrying the same token, neither file appears in the other's snapshot, and
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
    /// directory whose entire value is that a file in it means something. So the row snapshots the
    /// directory first and deletes, in a <c>finally</c>, the new files it can prove ARE ITS OWN —
    /// whether it passed, failed or threw — and reports the names it deleted in the failure
    /// message so the evidence survives the cleanup.
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
        // Arrange - the capture directory as it stands before this test runs. It may not exist
        // at all, which is the ordinary case on a machine that has never met #420.
        var directory = DcpCapture.ResolveDirectory();
        Assert.NotNull(directory);
        var before = ListCaptures(directory!);

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
            // row run's own leavings and nothing else. NewCapturesNaming applies the ownership
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
                mine = NewCapturesNaming(directory!, before, _healthyResourceName);
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
                // resolved path, and this row asserts a few lines further down that the ENGINE
                // must keep resolved paths out of a diagnostic (see (d) in the sibling row).
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
        // NewCapturesNaming's remarks on DcpCapture.WriteAsync - report a real engine regression
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
        var realBefore = ListCaptures(realDirectory!);

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
            var leaked = NewCapturesNaming(realDirectory!, realBefore, _failingResourceName);
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
            //     is an emptiness assertion over NewCapturesNaming, and emptiness assertions fail
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
            //     message embeds the WHOLE capture body, so Assert.True would have built that
            //     string on every green run. That it embeds the body at all is a separate open
            //     defect - a body can carry Aspire per-run generated passwords and absolute host
            //     paths into a public job log - tracked as #526 and deliberately not changed
            //     here.
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
                    + "* line, which means the buffer was empty by the time the gate failed - the "
                    + "arming window closed too early. Capture body:\n" + body);
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
    /// The captures that appeared in <paramref name="directory"/> since <paramref name="before"/>
    /// was taken AND that name <paramref name="resourceName"/> in their body — that is, the ones
    /// the calling row can prove are its own.
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
    /// <strong>Why both halves of the filter are needed.</strong> The <c>before</c> difference
    /// alone is the #489 defect: it is a claim about the whole shared directory, which other test
    /// hosts write to concurrently (class remarks carry the measurement). The name alone would
    /// trip over a capture an earlier run left behind. Together they say "written during my
    /// window, by my topology" — which is now literally what they say, because the suffix makes
    /// the name an instance key rather than a key to a topology of this KIND.
    /// </para>
    /// <para>
    /// <strong>WHICH directory this is, since this method both reads and — in one caller —
    /// deletes.</strong> Not necessarily a 0700 per-user root:
    /// <see cref="DcpCapture.ResolveDirectory()"/> lets <c>VOUCHFX_DCP_CAPTURE_DIR</c> outrank
    /// <c>LocalApplicationData</c>, and this project's own troubleshooting guide recommends
    /// pointing it at <c>${{ github.workspace }}/vouchfx-captures</c>. An operator following that
    /// recipe has this method enumerating, reading and deleting inside a workspace. The blast
    /// radius stays bounded by the two filters and the <c>dcp-capture-*.log</c> glob — nothing
    /// outside that name shape is ever touched — but it is not confined to a private directory,
    /// so it is stated rather than assumed away.
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
    /// capture it does not own — the filters exist to prevent exactly that. Misclassifying MY OWN
    /// file as foreign needs the row's own capture to be unreadable, which is reachable rather
    /// than impossible: <c>DcpCapture.WriteAsync</c> abandons a stalled write after five seconds
    /// without cancelling the I/O, and the file it left behind can still be open under
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
    private static string[] NewCapturesNaming(
        string directory, IReadOnlyCollection<string> before, string resourceName)
    {
        // A capture cannot be larger than the recorder's own budget plus its header, so anything
        // past this bound is not one of ours and is not worth reading. The bound is not thrift:
        // File.ReadAllText is unbounded and uncancellable and this runs inside a finally, and
        // EnumerateFiles will happily return a FIFO on Unix - reading one blocks for ever, and
        // DcpCapture.CreateDirectoryOwnerOnly deliberately does not narrow the mode of a
        // PRE-EXISTING directory, so a group-writable capture directory is possible. A FIFO also
        // reports length 0, which the lower bound rejects; a zero-length regular file is a
        // mid-write or a stub and is not a capture either.
        const int HeaderSlack = 8 * 1024;
        var maxCaptureBytes = (long)DcpFlightRecorder.DefaultCharLimit * 4 + HeaderSlack;

        return ListCaptures(directory)
            .Except(before, StringComparer.OrdinalIgnoreCase)
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
