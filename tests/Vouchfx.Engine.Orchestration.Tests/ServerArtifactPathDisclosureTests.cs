// Issue #473 — `security.serverArtifacts[].source` is recorded into the run's
// SecurityPathDisclosureLedger, so a resolved absolute host path in a diagnostic the ENGINE DID
// NOT WRITE is substituted back to the author's own text before it reaches an artefact.
//
// THE LEAK, AND WHY IT NEEDED THE LEDGER RATHER THAN A BETTER MESSAGE. ServerArtifactInjection's
// own throws already name the declared text by construction (#357) — a source that is blank,
// rooted, escaping or missing is diagnosed without disclosing anything. What it cannot constrain
// is what happens to the RESOLVED path after it hands it to Aspire's `WithContainerFiles`, which
// streams the bytes through the Docker daemon API at container-start time. A file that becomes
// unreadable, or a stage the daemon rejects, between Map's eager check and container start throws
// out of the Configure closure; SuiteTopology.cs's blanket `catch (Exception ex)` classifies it
// and its raw message becomes OrchestrationErrorInfo.Detail, which reaches the §14 event stream,
// the --events artifact, the JUnit `message` attribute and the HTML report.
//
// WHAT THESE TESTS MEASURE AND WHAT THEY DO NOT — stated plainly, because half of this is
// structural and pretending otherwise is worse than the gap.
//
//   MEASURED, deterministically and without Docker:
//     • `Plan` records the (resolved, declared) pair — proved through behaviour (the scrub
//       substitutes) rather than by reading the ledger's dictionary.
//     • The text the scrub is applied to is a REAL exception message produced by a REAL failure
//       on the REAL resolved path: the artefact file is opened with FileShare.None and read
//       again, which is precisely the "became unreadable between validation and container start"
//       shape, and the BCL builds the message quoting the absolute path it was given. No message
//       is fabricated anywhere in this file.
//     • The wiring one level up: EnvironmentMapper.Map threads the ledger to Plan for a service
//       AND for a dependency.
//
//   NOT MEASURED HERE: that the Docker daemon's OWN rejection text reaches
//     OrchestrationErrorInfo.Detail and is scrubbed there. Driving that needs a daemon that
//     accepts a stage and then fails it, which is not reproducible without Docker, and inventing
//     a fake daemon to manufacture a measurement would prove only that the fake behaves as
//     written. The two halves that ARE pinned — the pair is recorded, and the scrub removes the
//     resolved path from real third-party text — are what the substitution consists of; the
//     remaining link is that ScenarioRunner.ScrubDiagnostic runs over Detail, which
//     SecretObservationLeakPenetrationTests already covers for the same ledger.
using System;
using System.Collections.Generic;
using System.IO;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// A resolved <c>security.serverArtifacts[].source</c> path is substitutable out of third-party
/// diagnostic text (#473).
/// </summary>
public sealed class ServerArtifactPathDisclosureTests : IDisposable
{
    /// <summary>The author's own text — this half is NOT a disclosure and must SURVIVE.</summary>
    private const string DeclaredSource = "./certs/kafka.keystore.jks";

    private static readonly int[] s_brokerPorts = { 9093 };

    private readonly string _suiteDirectory;
    private readonly string _resolvedSource;

    public ServerArtifactPathDisclosureTests()
    {
        _suiteDirectory = Path.Combine(
            Path.GetTempPath(), "vouchfx-473-artefact-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(_suiteDirectory, "certs"));

        _resolvedSource = Path.Combine(_suiteDirectory, "certs", "kafka.keystore.jks");
        File.WriteAllBytes(_resolvedSource, new byte[] { 0xFE, 0xED, 0xFE, 0xED });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_suiteDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// After <c>Plan</c>, the run's ledger substitutes the author's declared text back into the
    /// REAL exception message the BCL builds when the artefact cannot be opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The absence is asserted BEFORE the presence.</strong> A break then fails on the
    /// leak — the thing that matters — rather than on a missing phrase, which is the convention
    /// <c>SecurityDiagnosticPathDisclosureTests</c> set and the reason the shared
    /// <see cref="HostPathDisclosure.AssertNoAbsoluteHostPath"/> is used here.
    /// </para>
    /// <para>
    /// <strong>The premise is asserted too.</strong> Before anything is concluded from the scrub,
    /// the RAW message is checked to contain the resolved path: without that, an arm asserting
    /// "no absolute host path in the scrubbed text" would pass for a message that never had one,
    /// and would keep passing with the recording deleted.
    /// </para>
    /// </remarks>
    [Fact]
    public void PlannedArtefact_RealUnreadableFileMessage_NamesTheDeclaredSourceOnly()
    {
        var ledger = new SecurityPathDisclosureLedger();

        ServerArtifactInjection.Plan(
            SecurityWith(DeclaredSource, "/etc/kafka/secrets/kafka.keystore.jks"),
            ownerKindPlural: "services",
            ownerName: "broker",
            resolvedSuiteDirectory: _suiteDirectory,
            pathDisclosures: ledger);

        var raw = RealUnreadableFileMessage();

        // The premise: this really is third-party text quoting the resolved absolute path.
        Assert.Contains(_resolvedSource, raw, StringComparison.Ordinal);

        var scrubbed = ledger.Scrub(raw)!;

        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the substituted container-file failure detail", scrubbed, _suiteDirectory);
        Assert.Contains(DeclaredSource, scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="EnvironmentMapper.Map"/> threads the ledger to <c>Plan</c> for a SERVICE.
    /// </summary>
    /// <remarks>
    /// The seam between the run and the recording site, asserted separately from the site itself:
    /// a <c>Plan</c> that records correctly and a <c>Map</c> that hands it no ledger produce
    /// exactly the leak this issue is about, and neither half's own test would see it.
    /// </remarks>
    [Fact]
    public void Map_ThreadsTheLedgerToTheArtefactsOfAService()
    {
        var ledger = new SecurityPathDisclosureLedger();

        EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(DeclaredSource, "/etc/kafka/secrets/kafka.keystore.jks")),
            suiteDirectory: _suiteDirectory,
            pathDisclosures: ledger);

        AssertSubstitutes(ledger);
    }

    /// <summary>
    /// <see cref="EnvironmentMapper.Map"/> threads the ledger to <c>Plan</c> for a DEPENDENCY.
    /// </summary>
    /// <remarks>
    /// Both owner kinds, because <c>Map</c> reaches <c>Plan</c> through two separate loops and an
    /// argument added to one of them is exactly the shape #364 recorded — an omission that
    /// compiles clean and reads correct.
    /// </remarks>
    [Fact]
    public void Map_ThreadsTheLedgerToTheArtefactsOfADependency()
    {
        var ledger = new SecurityPathDisclosureLedger();

        EnvironmentMapper.Map(
            DependencyEnv(SecurityWith(DeclaredSource, "/etc/kafka/secrets/kafka.keystore.jks")),
            suiteDirectory: _suiteDirectory,
            pathDisclosures: ledger);

        AssertSubstitutes(ledger);
    }

    /// <summary>
    /// A <see langword="null"/> ledger — every non-production <c>Map</c> call site — records
    /// nothing and throws nothing.
    /// </summary>
    /// <remarks>
    /// The default path is the one taken by ~60 existing call sites, so "the recording site does
    /// not require a ledger" is worth a line rather than an assumption.
    /// </remarks>
    [Fact]
    public void Plan_WithNoLedger_IsUnaffected()
    {
        var groups = ServerArtifactInjection.Plan(
            SecurityWith(DeclaredSource, "/etc/kafka/secrets/kafka.keystore.jks"),
            ownerKindPlural: "services",
            ownerName: "broker",
            resolvedSuiteDirectory: _suiteDirectory,
            pathDisclosures: null);

        Assert.Single(groups);
        Assert.Equal("/etc/kafka/secrets", groups[0].DestinationDirectory);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="ledger"/> to the real unreadable-file message and asserts the
    /// substitution — absence first, then presence.
    /// </summary>
    private void AssertSubstitutes(SecurityPathDisclosureLedger ledger)
    {
        var raw = RealUnreadableFileMessage();
        Assert.Contains(_resolvedSource, raw, StringComparison.Ordinal);

        var scrubbed = ledger.Scrub(raw)!;

        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the substituted container-file failure detail", scrubbed, _suiteDirectory);
        Assert.Contains(DeclaredSource, scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message the BCL itself builds when the planned artefact cannot be opened — the
    /// "unreadable between validation and container start" failure, produced rather than written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Produced, not fabricated, and that distinction is the point of this helper.</strong>
    /// A hand-written string containing <c>_resolvedSource</c> would test the ledger against text
    /// this repo invented; holding the file with <see cref="FileShare.None"/> and reading it again
    /// makes the operating system compose the message, quoting the absolute path it was actually
    /// handed. The path in it is therefore the same one <c>Plan</c> put in the ledger for the same
    /// reason a daemon's would be — because that is the path the engine gave away.
    /// </para>
    /// <para>
    /// Windows raises <see cref="IOException"/> ("used by another process") for the share
    /// violation; POSIX advisory locking does not, so this falls back to reading a path that has
    /// been removed, which raises <see cref="FileNotFoundException"/> naming the same absolute
    /// path. Either way the message is the platform's own and carries the resolved path, which is
    /// all this fixture claims — and the arm that consumes it asserts that premise before
    /// concluding anything.
    /// </para>
    /// </remarks>
    private string RealUnreadableFileMessage()
    {
        // Shape one: the file is held exclusively and read again. Windows enforces the share mode
        // and raises IOException naming the path; POSIX does not, and the read simply succeeds.
        try
        {
            using var exclusive = new FileStream(
                _resolvedSource, FileMode.Open, FileAccess.Read, FileShare.None);

            _ = File.ReadAllBytes(_resolvedSource);
        }
        catch (IOException ex) when (ex.Message.Contains(_resolvedSource, StringComparison.Ordinal))
        {
            return ex.Message;
        }

        // Shape two, for every platform the first does not cover: the artefact is REMOVED after
        // the eager check and before the read. Same window, same disclosure, and the BCL's message
        // names the same absolute path.
        File.Delete(_resolvedSource);
        try
        {
            _ = File.ReadAllBytes(_resolvedSource);
        }
        catch (IOException ex) when (ex.Message.Contains(_resolvedSource, StringComparison.Ordinal))
        {
            // FileNotFoundException derives from IOException, so this catches both shapes.
            return ex.Message;
        }

        // Neither shape produced a real message naming the resolved path, so there is nothing for
        // the substitution to act on. Fail loudly: an arm handed a message with no resolved path
        // in it would assert "no absolute host path" vacuously and stay green with the recording
        // deleted, which is the one outcome worse than a red test.
        throw new InvalidOperationException(
            "No real third-party message quoting the resolved artefact path could be produced on "
            + "this platform, so the arm consuming this would assert nothing. Re-point it rather "
            + "than leave it green.");
    }

    private static SecuritySpec SecurityWith(string source, string target) =>
        new(
            Profile: "mtls",
            Endpoint: "9093",
            CaCert: null,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: new[] { new SecurityServerArtifactSpec(source, target) });

    private static EnvironmentSpec ServiceEnv(SecuritySpec security) =>
        new(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["broker"] = new ServiceSpec(
                    Image: "acme/kafka:7.5.3",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = s_brokerPorts,
                    Security = security,
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static EnvironmentSpec DependencyEnv(SecuritySpec security) =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                {
                    Security = security,
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);
}
