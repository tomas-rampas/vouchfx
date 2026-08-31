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
///   successful start writes nothing - into a tautology. It deletes anything that did appear, so
///   a flaky Docker leg cannot leave an artefact a later reader mistakes for a real
///   finding.</item>
///   <item><c>Register_InsideTheRealAspireHost_...</c> is about log routing and has no business
///   writing captures at all, so it disarms the production recorder with
///   <c>VOUCHFX_DCP_CAPTURE=0</c> and brings its own.</item>
///   <item><c>AFailingTopology_WritesACaptureIntoTheRedirectedDirectory</c> must write a
///   capture - that is its property - so it REDIRECTS the production path to a scratch
///   directory with <c>VOUCHFX_DCP_CAPTURE_DIR</c> and asserts the real directory did not grow.
///   Redirecting rather than reaching past the production code is what keeps it a test of the
///   real flush, and it is the row that proves the arming window spans the health gates.</item>
/// </list>
/// <para>
/// All three restore whatever environment variable they set, and this assembly disables test
/// parallelism, so the process-wide mutation cannot race a sibling.
/// </para>
/// </remarks>
public sealed class DcpFlightRecorderDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

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
    /// directory first and deletes, in a <c>finally</c>, exactly what it added — whether it passed,
    /// failed or threw — and reports the names it deleted in the failure message so the evidence
    /// survives the cleanup.
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

        string[] added;
        try
        {
            // Act - a real, successful topology start through the production path, with the
            // recorder ARMED. Disarming here would turn the assertion below into a tautology.
            await using (var topology = await HeadlessTopology.StartAsync(
                appHostAssemblyName: AppHostAssemblyName,
                configureResources: b => b.AddContainer("whoami-dcp-capture", "traefik/whoami")))
            {
                Assert.NotNull(topology.Application);
            }
        }
        finally
        {
            // Whatever happened, leave the operator's directory as it was found. A capture
            // written by a failing Docker leg is a test artefact, and this directory's entire
            // value is that a file in it is a real finding rather than a fabricated one.
            added = ListCaptures(directory!)
                .Except(before, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var stray in added)
            {
                try
                {
                    File.Delete(stray);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        // Assert - nothing new. A healthy run pays a bounded in-memory buffer for the duration
        // of the start and the health gates, and nothing else: no file, no output, no residue.
        Assert.True(
            added.Length == 0,
            "A successful topology start wrote " + added.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            + " DCP capture file(s), which only the FAILURE path may do. They have been deleted "
            + "again so they cannot be mistaken for a real finding:\n  "
            + string.Join("\n  ", added));
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
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Register_InsideTheRealAspireHost_CapturesDcpTrafficAndNothingElse()
    {
        // Asserted, not assumed - as in the two sibling rows. Without it, a ResolveDirectory that
        // returned null would make ListCaptures return empty every time and BOTH of this row's
        // capture-directory assertions would pass vacuously, while claiming the disarm held.
        var directory = DcpCapture.ResolveDirectory();
        Assert.NotNull(directory);
        var before = ListCaptures(directory!);

        var original = Environment.GetEnvironmentVariable(DcpFlightRecorder.OptOutVariable);
        using var recorder = new DcpFlightRecorder();
        try
        {
            Environment.SetEnvironmentVariable(DcpFlightRecorder.OptOutVariable, "0");

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

        // The disarm held: the production recorder was never created, so nothing could be
        // written even had the start failed.
        Assert.Equal(before.Length, ListCaptures(directory!).Length);

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
    /// <strong>Assertion (c) is the one that matters.</strong> The capture written at the gate
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
                    ["never-ready"] = new ServiceSpec(
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

            // (a) A capture appears in the INJECTED directory ...
            var captures = ListCaptures(scratch);
            Assert.True(
                captures.Length == 1,
                "expected exactly one capture in the injected directory, found "
                + captures.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // ... and the operator's real directory is untouched.
            Assert.Equal(realBefore.Length, ListCaptures(realDirectory!).Length);

            // (b) The failure carries the location TOKEN and the tail - and not the resolved
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

            // (c) The capture holds DCP traffic rather than being an empty file with a header.
            //     See this row's remarks for what that does and does NOT establish about WHICH
            //     of the two flush sites produced it.
            var body = await File.ReadAllTextAsync(captures[0]);
            var dcpLines = body
                .Split('\n')
                .Where(l => l.Contains(
                    DcpFlightRecorder.DcpCategoryPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(
                dcpLines.Count > 0,
                "the capture written at the health-gate timeout contains no "
                + DcpFlightRecorder.DcpCategoryPrefix
                + "* line, which means the buffer was empty by the time the gate failed - the "
                + "arming window closed too early. Capture body:\n" + body);
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
