// Vouchfx.Cli.Tests — EDGE-007: the `--watch` path's security wiring and its session-scoped
// resolved-secret ledger (client-key-password, T8). No Docker.
//
// TWO KINDS OF TEST LIVE HERE, and the split is deliberate.
//
//   • BEHAVIOURAL — the two catches in WatchRunner.ProcessChangeGuardedAsync are the watch
//     loop's real emission path for a failed save, and that method is already reachable from a
//     unit test with a fake "process one change" action (see WatchRunnerGuardTests). So the
//     scrub is driven THROUGH the production sink with a value recorded by a REAL SecretAccessor
//     resolving a REAL reference — never by calling Scrub directly, which the T5 review measured
//     goes green against a production path that never calls it.
//
//   • STRUCTURAL — the rest of WatchRunner.RunAsync stands up an Aspire topology and cannot be
//     reached without Docker (#364: it is a second implementation of the run path with no
//     Docker-free seam). The censuses below read WatchRunner.cs and pin the wiring that no
//     Docker-free test can execute: that the probe's security accessor is built from the
//     production secret-accessor factory, that ONE ledger is created for the whole session and
//     outside the build seam, and that both the probe scope and the step path are handed THAT
//     ledger. Every one of those is an optional argument or a lambda-local variable — dropping
//     any of them compiles cleanly, changes no signature, and silently restores the
//     pre-EDGE-007 behaviour.
//
//     These censuses are ALSO the second half of the evidence for EDGE-007's behavioural
//     requirement. Vouchfx.Engine.Runtime.Tests/WatchProbeSecurityWiringTests executes a
//     hand-assembled REPLICA of the probe composition (it cannot see WatchRunner across the
//     assembly boundary); this file is what ties that replica to the real call site. Delete
//     either and the pair stops being evidence about `--watch`.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Cli;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchRunnerSecurityLedgerTests
{
    // ── Behavioural: the watch loop's own sinks scrub through the session ledger ───────────

    /// <summary>
    /// A passphrase resolved through the session ledger is redacted from the environment-error
    /// line the watch loop writes on a failed save. Reached through the production sink
    /// (<c>ProcessChangeGuardedAsync</c>'s <see cref="OrchestrationException"/> catch), with the
    /// value recorded the way production records it — a real accessor resolving a real reference.
    /// </summary>
    [Fact]
    public async Task OrchestrationErrorCarryingAResolvedPassphrase_IsScrubbedThroughTheSessionLedger()
    {
        var ledger = new ResolvedSecretLedger();
        const string passphrase = "watch-session-passphrase-7f3a";
        ResolveThroughRealAccessor(ledger, passphrase);

        var output = new StringWriter();
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: "broker",
            RegistryHost: null,
            AuthStatus: null,
            Detail: $"client identity load failed using '{passphrase}'");

        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new OrchestrationException(info, new InvalidOperationException("boom")),
            output,
            CancellationToken.None,
            ledger);

        var rendered = output.ToString();
        Assert.Contains("--watch: environment error during run", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(passphrase, rendered, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The catch-all sink — which fires for anything that is not an
    /// <see cref="OrchestrationException"/>, including the raw <see cref="ArgumentException"/> a
    /// malformed <c>environment</c> block throws — scrubs too. It is the more reachable of the
    /// two: it catches whatever a provider or the platform happens to throw, so it is the sink
    /// most likely to carry text nobody designed.
    /// </summary>
    [Fact]
    public async Task CatchAllErrorCarryingAResolvedPassphrase_IsScrubbedThroughTheSessionLedger()
    {
        var ledger = new ResolvedSecretLedger();
        const string passphrase = "watch-catchall-passphrase-91c2";
        ResolveThroughRealAccessor(ledger, passphrase);

        var output = new StringWriter();

        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new InvalidOperationException($"could not decrypt with '{passphrase}'"),
            output,
            CancellationToken.None,
            ledger);

        var rendered = output.ToString();
        Assert.Contains("--watch: error during run", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(passphrase, rendered, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// COMPOSITION ORDER, and it is the whole reason this arm exists: the ledger scrub must run
    /// BEFORE <c>DisplaySanitiser</c>, not after.
    /// </summary>
    /// <remarks>
    /// The sanitiser rewrites control bytes. A passphrase containing one therefore no longer
    /// matches the ledger's recorded form once the sanitiser has been over the text, so scrubbing
    /// second leaks the printable remainder — measurably, not theoretically: with the order
    /// inverted this arm's own assertions on <c>alpha</c> and <c>omega</c> both fail. Scrubbing
    /// first sees the value exactly as it was resolved, replaces it whole, and hands the
    /// sanitiser a marker it passes through unchanged.
    /// </remarks>
    [Fact]
    public async Task APassphraseCarryingAControlByte_IsScrubbedBeforeTheSanitiserRewritesIt()
    {
        var ledger = new ResolvedSecretLedger();
        var passphrase = "alpha" + (char)0x1B + "omega";
        ResolveThroughRealAccessor(ledger, passphrase);

        var output = new StringWriter();

        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new InvalidOperationException($"key rejected: {passphrase}"),
            output,
            CancellationToken.None,
            ledger);

        var rendered = output.ToString();

        // Neither half of the value survives — which is what "scrubbed as a whole, before the
        // sanitiser split it" means.
        Assert.DoesNotContain("alpha", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("omega", rendered, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, rendered, StringComparison.Ordinal);

        // …and the sanitiser still did its own job: no raw ESC byte reaches the terminal.
        Assert.DoesNotContain((char)0x1B, rendered);
    }

    /// <summary>
    /// Without a ledger the sinks behave exactly as they did before EDGE-007 — the parameter is
    /// additive, and every existing caller that omits it is unchanged.
    /// </summary>
    [Fact]
    public async Task WithNoLedger_TheSinksStillSanitiseAndReportAsBefore()
    {
        var output = new StringWriter();

        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new InvalidOperationException("plain failure"),
            output,
            CancellationToken.None);

        var rendered = output.ToString();
        Assert.Contains("--watch: error during run", rendered, StringComparison.Ordinal);
        Assert.Contains("plain failure", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretString.RedactedMarker, rendered, StringComparison.Ordinal);
    }

    // ── Structural: the wiring inside RunAsync, which needs Docker to execute ──────────────

    /// <summary>
    /// EDGE-007's first requirement, pinned structurally: the <c>--watch</c> probe's security
    /// accessor is built from the PRODUCTION secret-accessor factory, so a suite declaring
    /// <c>clientKeyPassword</c> resolves it against exactly the sources <c>vouchfx run</c> uses.
    /// </summary>
    /// <remarks>
    /// This is the argument that was omitted once already, invisibly — <c>Build</c>'s third
    /// parameter used to be optional, and leaving it off compiled and read correctly while every
    /// secured suite became unrunnable under <c>--watch</c>. The behavioural half of this
    /// requirement — that the composition actually loads an encrypted key, and refuses a wrong
    /// passphrase with EDGE-001's diagnostic — is
    /// <c>Vouchfx.Engine.Runtime.Tests.WatchProbeSecurityWiringTests</c>.
    /// </remarks>
    [Fact]
    public void TheWatchProbe_BuildsItsSecurityAccessorFromTheProductionSecretFactory()
    {
        var source = WatchRunnerSource();

        int buildCallSites = Regex.Matches(source, @"SecurityConfigurationAccessor\.Build\(").Count;
        Assert.Equal(1, buildCallSites);

        Assert.True(
            Regex.IsMatch(source, @"SecurityConfigurationAccessor\.Build\(\s*ast,\s*suiteDirectory,\s*probeSecrets\.Accessor\)"),
            "WatchRunner's build seam must pass the probe scope's accessor to "
            + "SecurityConfigurationAccessor.Build. Dropping it (the parameter used to be "
            + "optional) leaves every suite declaring clientKeyPassword unrunnable under "
            + "--watch, and fails blaming the author for the host's defect (EDGE-007).");

        Assert.DoesNotContain("secrets: null", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// EDGE-007's ledger seam: ONE <c>ResolvedSecretLedger</c> for the whole watch SESSION, built
    /// outside the build seam, and handed to BOTH the probe scope and the step path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session scope is the load-bearing part. State the failure precisely, because the obvious
    /// phrasing is wrong: the build seam is per-REBUILD, not per-save —
    /// <c>WatchSession.OnChangeAsync</c> reaches it only when the environment hash changes — so a
    /// seam-scoped ledger would still be shared by every reusing save. What breaks is the REBUILD
    /// save itself: the watch loop's sinks capture the ledger by value before the seam runs, so
    /// the probe would resolve into an instance the catch receiving its failure never holds.
    /// </para>
    /// <para>
    /// Every clause here is a shape that compiles when broken. Passing no ledger to the factory
    /// compiles (optional parameter); passing <c>new ResolvedSecretLedger()</c> at either site
    /// compiles and silently reinstates the private-ledger behaviour; omitting
    /// <c>sharedLedger:</c> on the run seam compiles (optional parameter) and leaves the step
    /// path building a ledger of its own.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWatchSession_SharesOneLedgerBetweenTheProbeAndTheStepPath()
    {
        var source = WatchRunnerSource();

        // Exactly one ledger is ever constructed in this file…
        var constructions = Regex.Matches(source, @"new ResolvedSecretLedger\(\)");
        int constructionCount = constructions.Count;
        Assert.Equal(1, constructionCount);

        // …and it is constructed BEFORE the build seam, not inside it.
        //
        // This clause is the only guard against a regression that keeps every other count in this
        // test intact, and it is reachable: declaring the variable as
        // `ResolvedSecretLedger sessionSecretLedger = null!;` here and assigning it as the first
        // statement of the build seam COMPILES, leaves exactly one construction, and hands every
        // sink a null on the very save the probe resolves.
        var buildSeam = source.IndexOf("buildTopologyAsync:", StringComparison.Ordinal);
        Assert.True(buildSeam > 0, "the build seam marker must exist in WatchRunner.cs");
        Assert.True(
            constructions[0].Index < buildSeam,
            "the watch session's ResolvedSecretLedger must be constructed OUTSIDE the build "
            + "seam. Constructed inside it, the sinks — which capture the ledger by value before "
            + "the seam runs — hold a different instance from the one the probe resolves into on "
            + "the rebuild save, so the catch that exists to receive a probe failure scrubs "
            + "against the wrong object (EDGE-007).");

        // The probe scope records into it: no argument-less factory call survives, and the one
        // call names the session ledger rather than any other expression. Whitespace-tolerant —
        // the call site is near the line-length limit and a reflow must not redden a correct tree.
        int argumentless = Regex.Matches(source, @"CreateSecretAccessorScope\(\s*\)").Count;
        Assert.Equal(0, argumentless);

        int sessionScoped =
            Regex.Matches(source, @"CreateSecretAccessorScope\(\s*sessionSecretLedger\s*\)").Count;
        Assert.Equal(1, sessionScoped);

        // And the step path is handed the SAME instance through the run seam.
        Assert.Contains("sharedLedger: sessionSecretLedger", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY write this class makes — to the injected writer or to the console — is accounted
    /// for: enumerated from the writes themselves, then classified, so a sink that calls no
    /// helper at all cannot hide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is enumerated from writes rather than from helper calls.</strong> The
    /// first version of this gate counted <c>ScrubThenSanitise</c> calls and
    /// <c>SanitiseForDisplay</c> calls, and claimed on that basis that every post-probe sink went
    /// through the helper. It could not have detected the counter-example, which was present and
    /// green at the time: the <c>could not read</c> IOException sink wrote raw, calling neither.
    /// A census that can only see the sites that already comply is not a census. This one starts
    /// from the WRITE CALLS and forces every site into a named bucket, so a new raw sink moves
    /// <c>raw</c> and a new helper-less one moves <c>total</c>.
    /// </para>
    /// <para>
    /// The one legitimately raw site interpolates a single <see langword="int"/> and no
    /// author-derived text at all; it is pinned by content, not merely counted, so "raw" cannot
    /// quietly come to mean something else.
    /// </para>
    /// <para>
    /// <strong>The enumeration covers the console too, not just the injected writer.</strong>
    /// Restricted to <c>output.Write…</c> this gate would have had the SAME shape of blindness it
    /// was built to remove, one level out: a <c>Console.WriteLine</c> or
    /// <c>Console.Error.WriteLine</c> added to this class would move no counter and fail nothing.
    /// No such sink exists today, so that hole is latent rather than open — which is exactly when
    /// it is cheap to close. Two nets: the write pattern itself accepts a console target, and a
    /// separate assertion forbids <c>Console.</c> outright.
    /// </para>
    /// <para>
    /// <strong>Known assumption in the extractor.</strong> Each write's statement is sliced to the
    /// FIRST <c>;</c> after it, which would end the statement early if a string literal in the
    /// arguments contained a semicolon. It cannot misclassify a compliant sink as raw today,
    /// because <c>ScrubThenSanitise(</c> is the first argument at all four scrubbing sinks and so
    /// precedes any literal; it is recorded rather than fixed because the fix (a brace/paren
    /// matcher) costs more than the failure mode is worth.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryWriteThisClassMakes_IsAccountedForByBucket()
    {
        var source = WatchRunnerSource();

        // Every write site, taken from the WRITE — not from any helper it may or may not call.
        // The target alternation covers the injected writer AND the console, so a sink that
        // bypasses `output` entirely still lands in a bucket instead of vanishing.
        var writes = Regex.Matches(
            source, @"(?:output|Console(?:\.Out|\.Error)?)\.Write(?:Line)?(?:Async)?\(");
        Assert.Equal(7, writes.Count);

        // Second net, independent of the extractor: this class writes through the writer it was
        // handed, never the ambient console. A Console call here would also escape the CLI's
        // --json/--events redirection, not merely this gate.
        int consoleUses = Regex.Matches(source, @"\bConsole\s*\.").Count;
        Assert.Equal(0, consoleUses);

        var scrubbed = new List<string>();
        var sanitisedOnly = new List<string>();
        var raw = new List<string>();

        foreach (Match write in writes)
        {
            // The statement this write begins, up to its terminating semicolon.
            var end = source.IndexOf(';', write.Index);
            Assert.True(end > write.Index, "every write site must terminate in a statement");
            var statement = source[write.Index..end];

            if (statement.Contains("ScrubThenSanitise(", StringComparison.Ordinal))
            {
                scrubbed.Add(statement);
            }
            else if (statement.Contains("SanitiseForDisplay(", StringComparison.Ordinal))
            {
                sanitisedOnly.Add(statement);
            }
            else
            {
                raw.Add(statement);
            }
        }

        // Four post-probe sinks scrub then sanitise: the two catches in ProcessChangeGuardedAsync,
        // the WatchSession report sink, and the could-not-read sink reached on every save.
        Assert.Equal(4, scrubbed.Count);

        // Two write before anything can have been resolved, so they sanitise only: the
        // did-not-parse message (selection time) and the watching banner (before the first run).
        Assert.Equal(2, sanitisedOnly.Count);

        // Exactly one raw write, and it is raw because it has nothing untrusted to render inert.
        var onlyRaw = Assert.Single(raw);
        Assert.Contains("--watch requires exactly one", onlyRaw, StringComparison.Ordinal);
        Assert.Contains("{selected.Count}", onlyRaw, StringComparison.Ordinal);
        Assert.DoesNotContain("filePath", onlyRaw, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", onlyRaw, StringComparison.Ordinal);

        // The four scrubbing sinks, named individually so a move is legible in the failure rather
        // than showing up only as a changed count.
        Assert.Contains(
            "ScrubThenSanitise($\"--watch: environment error during run: {oex.Message}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrubThenSanitise(line, sessionSecretLedger)", source, StringComparison.Ordinal);
        Assert.Contains(
            "$\"--watch: could not read '{filePath}': {ex.Message}\"",
            source,
            StringComparison.Ordinal);

        // And the helper is used ONLY by those writes — no other caller composes it differently.
        // Call sites only; the declaration is excluded by its parameter list's leading type.
        var helperCallSites = Regex.Matches(source, @"ScrubThenSanitise\((?!string\b)").Count;
        Assert.Equal(4, helperCallSites);
    }

    /// <summary>
    /// The behavioural half of EDGE-007's evidence must still exist. This gate turns the
    /// convention both files describe at length into something that FAILS when broken.
    /// </summary>
    /// <remarks>
    /// The pair is forced by #364 and by the assembly boundary, not chosen: no single test project
    /// can both execute the probe composition (needs <c>Vouchfx.Engine.Runtime</c> internals) and
    /// read <c>WatchRunner</c>'s call site (needs <c>vouchfx</c> internals). So the censuses in
    /// this file pin a call site whose BEHAVIOUR is proved only by a replica in the other file,
    /// and deleting that replica would leave these gates green while EDGE-007 lost its behavioural
    /// evidence entirely — the failure would be silent, which is the one kind this todo exists to
    /// remove. Asserted by path rather than by type because the type is not referenceable from
    /// here; when #364 closes and the watch path gains a real seam, both halves and this guard
    /// should go together.
    /// </remarks>
    [Fact]
    public void TheBehaviouralHalfOfTheEvidence_StillExists()
    {
        var replica = Path.Combine(
            RepositoryRoot(), "tests", "Vouchfx.Engine.Runtime.Tests",
            "WatchProbeSecurityWiringTests.cs");

        Assert.True(
            File.Exists(replica),
            $"'{replica}' is missing. It executes the probe composition this file's censuses "
            + "only pin textually; without it, EDGE-007 has structural evidence and no "
            + "behavioural evidence, and these gates would stay green while saying nothing about "
            + "whether an encrypted client key actually loads under --watch.");

        // Not merely present — still carrying the two arms that matter.
        var text = File.ReadAllText(replica);
        Assert.Contains(
            "ForTheWatchProbeComposition_EncryptedKeyAndCorrectPassphrase_LoadsTheClientIdentity",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "ForTheWatchProbeComposition_WrongPassphrase_RaisesEdge001RatherThanConnectingAnonymously",
            text,
            StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Records <paramref name="value"/> in <paramref name="ledger"/> the way production records
    /// it: a real <see cref="SecretAccessor"/> over the real <c>env</c> resolver, resolving a real
    /// <c>${secret:env/…}</c> reference. Recording by hand would test the sink against a ledger
    /// state production never produces.
    /// </summary>
    private static void ResolveThroughRealAccessor(ResolvedSecretLedger ledger, string value)
    {
        // Process-global, and xUnit runs classes in parallel — so the name must be unique.
        var variable = "VOUCHFX_WATCH_LEDGER_TEST_" + Guid.NewGuid().ToString("n");
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }),
                ledger);

            var resolved = accessor.Resolve("${secret:env/" + variable + "}");

            // Not vacuous: the value really did reach the ledger.
            Assert.Equal(SecretString.RedactedMarker, resolved.ToString());
            Assert.Equal(1, ledger.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static string WatchRunnerSource() => File.ReadAllText(
        Path.Combine(RepositoryRoot(), "src", "Cli", "Vouchfx.Cli", "Watch", "WatchRunner.cs"));

    /// <summary>
    /// Walks up from the test binary to the repository root (the directory holding
    /// <c>vouchfx.sln</c>) — the same discovery shape this project's golden gates use.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "could not locate the repository root (no ancestor directory of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').");
    }
}
