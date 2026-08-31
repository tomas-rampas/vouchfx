// Vouchfx.Engine.Runtime.Tests — issue #375: a resolved security-material path this engine hands
// to a third-party client library must not survive into an archived diagnostic.
//
// WHY THIS FILE EXISTS BESIDE SecurityDiagnosticPathDisclosureTests, WHICH ALREADY ASSERTS THE
// PROPERTY. That file covers the diagnostics the ENGINE writes, at validation time, and its
// property assertion passed on the pre-fix tree because every one of those sites already complied
// with #357's declared-path-only rule. The leak #375 records is at a site the engine does not
// write: librdkafka is handed `ssl.ca.location` / `ssl.certificate.location` / `ssl.key.location`
// as resolved absolute paths (REQ-015 accepts nothing else), and on a load failure it builds its
// own message quoting them back. That message arrives as a caught exception inside a Kafka
// provider's guarded region, becomes the step's Observation, and is archived into the §14 event
// stream, the --events artifact and the HTML report.
//
// MEASURED RED-FIRST, by mutation drill against the fixed tree rather than by assertion:
//
//   * Neutering SecurityPathDisclosureLedger.Scrub to `return text;` reddens 5 of the 15 tests here
//     — the three channel tests (step observation, scenario-completed message, environment-error
//     detail) and the two ledger-contract tests (longest-first ordering, JSON-escaped form).
//     10 pass, because they assert either the ledger's recording contract or the pre-fix leak.
//   * Neutering SecurityConfigurationAccessor.WithDeclaredPaths to `return message;` reddens the
//     LoadClient TOCTOU arm, 1 failed / 0 passed.
//
// Both mutations were reverted immediately; the numbers above are what the runs printed.
// StepObservation_WithNoPathLedger_StillLeaks keeps the pre-fix behaviour as a permanent arm, so
// the fixed-path assertions cannot quietly become vacuous.
//
// THE PRODUCTION METHODS, NOT A RECONSTRUCTION. Every channel test drives
// ScenarioRunner.BuildStepObservation / ScrubDiagnostic / EnvironmentErrorLine — the same members
// StepEventBuilder and the runner's own catch blocks call — so a green test here is a green
// production path. The accessor is a REAL SecurityConfigurationAccessor over REAL generated
// certificate material, so the registration under test is the one that runs, not a hand-seeded
// ledger standing in for it.
//
// DOCKER-FREE by construction: no topology, no broker, no registry. The librdkafka text is
// synthesised in the exact shape the library produces, because reproducing the real failure would
// require a broker and would prove nothing this does not.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Issue #375: the run's <see cref="SecurityPathDisclosureLedger"/> substitutes the author's
/// declared text back over any resolved security-material path that reaches an archived
/// diagnostic, on every channel the resolved-secret ledger already guards.
/// </summary>
public sealed class SecurityPathDisclosureLedgerTests
{
    private const string Run = "run-375";

    /// <summary>
    /// CA1861, and the same separator set <c>SecurityDiagnosticPathDisclosureTests</c> uses: the
    /// property assertion below is deliberately the same predicate, so the two files cannot come
    /// to disagree about what "names an absolute host path" means.
    /// </summary>
    private static readonly char[] s_tokenSeparators =
        { ' ', '\t', '\r', '\n', '"', '\'', '<', '>', '&', ';', ',', '(', ')', '[', ']' };

    private static readonly char[] s_pathSeparators = { '\\', '/' };

    private static readonly char[] s_trailingPunctuation = { '.', ':' };

    private static SecuritySpec MtlsSecurity() =>
        new(
            Profile: "mtls",
            Endpoint: "9093",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: TestCertificateAuthority.ClientCertFileName,
            ClientKey: TestCertificateAuthority.ClientKeyFileName,
            ServerArtifacts: null);

    private static ScenarioAst AstWithSecuredService(string serviceName, SecuritySpec security) =>
        new(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: new System.Collections.Generic.Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
                {
                    [serviceName] = new ServiceSpec(
                        Image: "confluentinc/cp-kafka:7.6.0",
                        Project: null,
                        ImagePullPolicy: null,
                        HttpPort: null,
                        Env: null)
                    {
                        Security = security,
                    },
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

    /// <summary>
    /// The text librdkafka produces when it cannot open a configured PEM, in the shape it
    /// produces it: the config key, the resolved absolute path, and the platform's reason.
    /// </summary>
    /// <remarks>
    /// Synthesised rather than provoked. The real path to this string needs a broker, a TLS
    /// listener and a mid-run file deletion; what is under test is what the engine does with the
    /// string once it has it, and that is identical either way. The SHAPE is what matters — the
    /// path appears as a bare token inside a longer sentence, which is exactly the case a
    /// substring substitution has to get right.
    /// </remarks>
    private static string LibrdkafkaFailureText(string resolvedKeyPath) =>
        "ssl.key.location failed: " + resolvedKeyPath
        + ": error:80000002:system library::No such file or directory";

    /// <summary>
    /// THE #375 CASE. A resolved client-key path handed out through the accessor's path view — the
    /// one view librdkafka can use — is substituted back to the author's declared text when the
    /// library's own failure message travels through the step-observation chokepoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both halves are asserted, and the second is the one that distinguishes this fix
    /// from a redaction.</strong> The absolute path must be gone AND the declared text must be
    /// present: blanking the path to <c>[REDACTED]</c> would satisfy the first assertion and
    /// leave the author with a diagnostic they cannot act on, which is the shape #357 rejected
    /// for the engine's own messages and this ledger exists not to reintroduce.
    /// </para>
    /// <para>
    /// The observation is a JSON string here because <c>BuildStepObservation</c> parses its input
    /// — a non-JSON observation degrades to <see langword="null"/> and would make this test pass
    /// vacuously. The <c>Assert.NotNull</c> below is what stops that.
    /// </para>
    /// </remarks>
    [Fact]
    public void StepObservation_CarryingALibrdkafkaPathFailure_SubstitutesTheDeclaredPath()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var ledger = new SecurityPathDisclosureLedger();
        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("events", MtlsSecurity()),
            bed.SuiteDirectory,
            secrets: null,
            pathDisclosures: ledger);
        try
        {
            // The registration under test: reading the path view is what a Kafka provider does,
            // and it is what puts (resolved -> declared) into the run's ledger.
            var certificates = accessor.For("events")!.Certificates!;
            var resolvedKeyPath = certificates.ClientKeyPath!;

            Assert.True(Path.IsPathRooted(resolvedKeyPath));
            Assert.Equal(1, ledger.Count);

            var raw = JsonSerializer.Serialize(new { error = LibrdkafkaFailureText(resolvedKeyPath) });

            var observation = ScenarioRunner.BuildStepObservation(
                NullSecretAccessor.Instance, ledger, raw);

            Assert.NotNull(observation);
            var rendered = observation!.Value.GetRawText();

            AssertNoAbsoluteHostPath("step observation", rendered, bed.SuiteDirectory);
            Assert.Contains(
                TestCertificateAuthority.ClientKeyFileName,
                rendered,
                StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The same text with NO ledger passed is the pre-fix behaviour, asserted so the test above
    /// cannot silently become vacuous — if a future change made the librdkafka shape stop
    /// containing a rooted path, this arm fails and says so.
    /// </summary>
    /// <remarks>
    /// This is the red-first measurement kept as a permanent arm rather than a note. It is the
    /// only assertion in this file that a leak IS possible; every other one asserts it is closed.
    /// </remarks>
    [Fact]
    public void StepObservation_WithNoPathLedger_StillLeaks_WhichIsWhatTheLedgerFixes()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("events", MtlsSecurity()),
            bed.SuiteDirectory,
            secrets: null,
            pathDisclosures: null);
        try
        {
            var resolvedKeyPath = accessor.For("events")!.Certificates!.ClientKeyPath!;
            var raw = JsonSerializer.Serialize(new { error = LibrdkafkaFailureText(resolvedKeyPath) });

            var observation = ScenarioRunner.BuildStepObservation(
                NullSecretAccessor.Instance, pathLedger: null, raw);

            Assert.NotNull(observation);

            // JSON-escaped, because the observation has been round-tripped through the serialiser
            // — which is exactly why the ledger scrubs the encoded form as well as the raw one.
            Assert.Contains(
                JsonEncodedText.Encode(bed.SuiteDirectory).ToString(),
                observation!.Value.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The scenario-level cause channel (<c>ScenarioCompletedEvent.Message</c>) — the JUnit
    /// <c>message</c> attribute and the HTML report's scenario section both read it.
    /// </summary>
    [Fact]
    public void ScenarioCompletedLine_SubstitutesTheDeclaredPath()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var ledger = new SecurityPathDisclosureLedger();
        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("events", MtlsSecurity()),
            bed.SuiteDirectory,
            secrets: null,
            pathDisclosures: ledger);
        try
        {
            var resolvedCaPath = accessor.For("events")!.Certificates!.CaCertificatePath!;

            var line = StepEventBuilder.ScenarioCompletedLine(
                Run,
                DateTimeOffset.UnixEpoch,
                "kafka-suite",
                Verdict.EnvironmentError,
                new VerdictCounts { EnvError = 1 },
                ledger: null,
                pathLedger: ledger,
                LibrdkafkaFailureText(resolvedCaPath));

            AssertNoAbsoluteHostPath("scenario-completed message", line, bed.SuiteDirectory);
            Assert.Contains(TestCertificateAuthority.CaFileName, line, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The environment-error channel — the one the topology probe's failures travel down.
    /// </summary>
    [Fact]
    public void EnvironmentErrorLine_SubstitutesTheDeclaredPath()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var ledger = new SecurityPathDisclosureLedger();
        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("events", MtlsSecurity()),
            bed.SuiteDirectory,
            secrets: null,
            pathDisclosures: ledger);
        try
        {
            var resolvedCertPath = accessor.For("events")!.Certificates!.ClientCertificatePath!;

            var line = ScenarioRunner.EnvironmentErrorLine(
                sharedLedger: null,
                sharedPathLedger: ledger,
                new OrchestrationErrorInfo(
                    Kind: OrchestrationErrorKind.SecurityConfirmation,
                    ResourceName: "events",
                    RegistryHost: null,
                    AuthStatus: null,
                    Detail: LibrdkafkaFailureText(resolvedCertPath)),
                Run,
                DateTimeOffset.UnixEpoch);

            AssertNoAbsoluteHostPath("environment-error detail", line, bed.SuiteDirectory);
            Assert.Contains(TestCertificateAuthority.ClientCertFileName, line, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// THE DOCUMENTED SIBLING GAP, and the arm that MEASURED it: <c>LoadClient</c>'s catch folds
    /// the PLATFORM's exception message into a <c>SecurityMaterialException</c>, and
    /// <c>X509Certificate2.CreateFromPemFile</c> opens its files through <c>System.IO</c>, so a
    /// missing one throws a <see cref="FileNotFoundException"/> carrying .NET's own
    /// "Could not find file 'C:\...\client-key.pem'." text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reached through the real TOCTOU window, not a synthetic throw.</strong>
    /// <c>EnvironmentSecurityValidator</c> existence-checks every declared path pre-topology, so
    /// the only way to reach this catch is for the file to become unreadable between that check
    /// and the load. Deleting it after the accessor is constructed reproduces exactly that, which
    /// is why this arm is worth more than a mocked exception: it proves the window is reachable as
    /// well as that the message is clean.
    /// </para>
    /// <para>
    /// <strong>LoadClient and NOT LoadCa, and the difference was measured rather than assumed.</strong>
    /// The investigation that framed this fix named <c>LoadCa</c>. On this host (.NET 8,
    /// Windows 10.0.26200) <c>LoadCa</c>'s <c>new X509Certificate2(string)</c> goes through CryptoAPI
    /// and its message is path-free for every reachable cause — missing file, locked file, path is a
    /// directory — so a test written against it would have passed on the UNFIXED tree and guarded
    /// nothing. <c>CreateFromPemFile</c> is the one that leaks. The fix is applied at both catches
    /// (their filters admit the same three exception types), but only this arm can prove it.
    /// </para>
    /// <para>
    /// The run-scoped ledger is deliberately NOT passed. <c>LoadClient</c> reaches the files through
    /// the certificate view, which never calls <c>ResolvedIfContained</c>, so nothing would have been
    /// registered — and this arm therefore also pins that the throw-site fix does not depend on the
    /// shared ledger having seen the path first.
    /// </para>
    /// </remarks>
    [Fact]
    public void LoadClient_WhenTheKeyVanishesAfterValidation_NamesOnlyTheDeclaredPath()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("events", MtlsSecurity()),
            bed.SuiteDirectory,
            secrets: null,
            pathDisclosures: null);
        try
        {
            var certificates = accessor.For("events")!.Certificates!;

            // The TOCTOU window: the file existed when the validator checked it and does not
            // exist now, which is the only way production reaches LoadClient's catch.
            File.Delete(Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientKeyFileName));

            var ex = Assert.Throws<SecurityMaterialException>(() => _ = certificates.ClientCertificate);

            // NOT VACUOUS: the platform text this message wraps really does name the path, so the
            // assertion below has something to fail on. Proved in-line rather than in prose.
            Assert.Contains(
                bed.SuiteDirectory,
                ex.InnerException!.Message,
                StringComparison.OrdinalIgnoreCase);

            AssertNoAbsoluteHostPath("SecurityMaterialException.Message", ex.Message, bed.SuiteDirectory);
            Assert.Contains(
                TestCertificateAuthority.ClientKeyFileName, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The ledger's own contract: a resolved path equal to its declared text, an empty declared
    /// text, and a null on either side are all ignored rather than recorded.
    /// </summary>
    /// <remarks>
    /// Each of the three would corrupt text rather than protect it. An empty resolved path matches
    /// at every position; an empty declared form deletes the path instead of replacing it; a
    /// resolved path equal to its declared text substitutes a string for itself.
    /// </remarks>
    [Theory]
    [InlineData(null, "ca.pem")]
    [InlineData("/tmp/ca.pem", null)]
    [InlineData("", "ca.pem")]
    [InlineData("/tmp/ca.pem", "")]
    [InlineData("   ", "ca.pem")]
    [InlineData("/tmp/ca.pem", "   ")]
    [InlineData("ca.pem", "ca.pem")]
    public void Record_DegenerateInputs_AreIgnored(string? resolved, string? declared)
    {
        var ledger = new SecurityPathDisclosureLedger();
        ledger.Record(resolved, declared);
        Assert.Equal(0, ledger.Count);
    }

    /// <summary>
    /// Nothing recorded means the input reference comes back unchanged: the scrub is a targeted
    /// substitution, never a blanket rewrite.
    /// </summary>
    [Fact]
    public void Scrub_WithNothingRecorded_ReturnsTheInputUnchanged()
    {
        var ledger = new SecurityPathDisclosureLedger();
        const string Text = "nothing here resolves to anything";

        Assert.Same(Text, ledger.Scrub(Text));
        Assert.Null(ledger.Scrub(null));
    }

    /// <summary>
    /// A recorded DIRECTORY that is a prefix of a recorded FILE must not pre-empt the file's own
    /// substitution and strand the tail of it — the ordinary shape when <c>caCert</c> and
    /// <c>clientCert</c> sit in one folder.
    /// </summary>
    /// <remarks>
    /// Longest-first ordering is what makes this hold, and it is asserted here rather than left to
    /// the implementation's comment: a shortest-first pass turns
    /// <c>/suite/certs/client.pem</c> into <c>certs/client.pem</c> only by accident of the two
    /// declared forms happening to compose, and produces garbage as soon as they do not.
    /// </remarks>
    [Fact]
    public void Scrub_LongestFormFirst_DoesNotStrandTheTailOfALongerPath()
    {
        var ledger = new SecurityPathDisclosureLedger();
        ledger.Record("/suite/certs", "certs");
        ledger.Record("/suite/certs/client.pem", "certs/client.pem");

        Assert.Equal(
            "could not open certs/client.pem",
            ledger.Scrub("could not open /suite/certs/client.pem"));
    }

    /// <summary>
    /// A declared text that CONTAINS another entry's recorded form is left intact: the scan never
    /// revisits text it has already substituted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sequential shape this replaced — one <c>string.Replace</c> per recorded form, each over
    /// the whole accumulated result — rewrites the declared text an earlier form just spliced in.
    /// Here the first entry substitutes <c>sub/suite/ca.pem</c>, and the second entry's recorded
    /// form <c>suite/</c> then occurs INSIDE that replacement, turning it into <c>subca.pem</c>: a
    /// file named after nothing, in the one diagnostic the author is meant to act on.
    /// </para>
    /// <para>
    /// <strong>CONSTRUCTED, not production-reachable, and saying so is the point.</strong> Every
    /// form today's callers record is a rooted absolute path and every replacement is the author's
    /// relative text, so no replacement can contain a form and this arm cannot fire from the
    /// engine. It is pinned because <c>Record</c> is an ordinary internal method with no such
    /// constraint on it, and because a guard whose only evidence is "the callers happen not to do
    /// that" stops being evidence the first time a caller changes.
    /// </para>
    /// </remarks>
    [Fact]
    public void Scrub_WhereADeclaredTextContainsAnotherRecordedForm_DoesNotRewriteItsOwnOutput()
    {
        var ledger = new SecurityPathDisclosureLedger();
        ledger.Record("/host/x/ca.pem", "sub/suite/ca.pem");
        ledger.Record("suite/", "");            // ignored: an empty declared text is never recorded
        ledger.Record("suite/ca", "REWRITTEN");

        var scrubbed = ledger.Scrub("could not open /host/x/ca.pem");

        Assert.Equal("could not open sub/suite/ca.pem", scrubbed);
        Assert.DoesNotContain("REWRITTEN", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ZERO-LENGTH recorded form is dropped rather than matched: it would match vacuously at
    /// every position and the single pass would never advance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>string.CompareOrdinal(text, index, form, 0, 0)</c> returns 0, so an empty form matches
    /// everywhere. Without the filter in <c>Scrub</c> the scan takes that match, appends the
    /// replacement, advances by zero, and repeats forever — an infinite loop growing a
    /// <c>StringBuilder</c> until the process dies.
    /// </para>
    /// <para>
    /// <strong>This is a FAILURE-MODE regression the guard repays, not merely a missing check.</strong>
    /// The <c>string.Replace</c> shape the single pass replaced threw <c>ArgumentException</c> on
    /// an empty <c>oldValue</c>, immediately and by name. Silently hanging instead is strictly
    /// worse than crashing, which is why this is worth a test rather than a comment.
    /// </para>
    /// <para>
    /// <strong>CONSTRUCTED VIA REFLECTION, because the front door is shut.</strong> <c>Record</c>
    /// rejects null, empty and whitespace on both sides, so no API path can seed an empty form and
    /// this arm cannot be written honestly without reaching past that guard. The reflection is
    /// deliberate and is asserted to have WORKED — a renamed field fails the lookup loudly rather
    /// than leaving the arm passing over an empty ledger, which is the way a reflection test
    /// usually rots.
    /// </para>
    /// <para>
    /// <strong>BOUNDED, so a regression fails instead of wedging the suite.</strong> The whole
    /// defect is non-termination; calling <c>Scrub</c> directly on a regressed build would hang
    /// this assembly rather than report anything. Racing it against a delay converts the hang back
    /// into the loud failure the original <c>string.Replace</c> gave for free. On a regressed
    /// build the worker keeps spinning after this test fails — an accepted cost, because the
    /// alternative is a run that never reports at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Scrub_WithAZeroLengthRecordedForm_DropsItRatherThanLoopingForever()
    {
        var ledger = new SecurityPathDisclosureLedger();
        ledger.Record("/host/x/ca.pem", "certs/ca.pem");

        var field = typeof(SecurityPathDisclosureLedger).GetField(
            "_declaredByResolved",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.True(
            field is not null,
            "SecurityPathDisclosureLedger._declaredByResolved was not found. This arm reaches past "
            + "Record's own guard to seed a form Record refuses, so a renamed field must fail here "
            + "rather than silently leave the arm asserting nothing about an empty ledger.");

        var table = (Dictionary<string, string>)field!.GetValue(ledger)!;
        table[string.Empty] = "SHOULD-NEVER-APPEAR";

        Assert.Equal(2, ledger.Count);   // not vacuous: the empty form really is recorded

        var worker = Task.Run(() => ledger.Scrub("could not open /host/x/ca.pem"));
        var finished = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            ReferenceEquals(finished, worker),
            "Scrub did not return within 10 seconds with a zero-length form recorded. That is the "
            + "non-termination this guard exists to prevent: an empty form matches at every "
            + "position, so the scan appends its replacement and advances by zero forever.");

        var scrubbed = await worker;
        Assert.Equal("could not open certs/ca.pem", scrubbed);
        Assert.DoesNotContain("SHOULD-NEVER-APPEAR", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The JSON-escaped form is scrubbed too, and replaced by the ESCAPED declared text — a raw
    /// replacement spliced into already-serialised JSON produces text no consumer can decode.
    /// </summary>
    /// <remarks>
    /// Not a theoretical case on Windows, where every resolved path is full of <c>\</c> and
    /// therefore differs from its encoded form. Written with an explicit backslash path so the
    /// assertion means the same thing on a POSIX CI runner as it does on the maintainer's host.
    /// </remarks>
    [Fact]
    public void Scrub_MatchesTheJsonEscapedForm_AndReplacesItWithTheEscapedDeclaredForm()
    {
        var resolved = string.Create(
            CultureInfo.InvariantCulture, $"C:{'\\'}suite{'\\'}certs{'\\'}ca.pem");

        var ledger = new SecurityPathDisclosureLedger();
        ledger.Record(resolved, "certs/ca.pem");

        var serialised = JsonSerializer.Serialize(new { error = "cannot open " + resolved });
        Assert.DoesNotContain(resolved, serialised, StringComparison.Ordinal);  // it is escaped

        var scrubbed = ledger.Scrub(serialised);

        Assert.DoesNotContain("suite", scrubbed, StringComparison.Ordinal);
        Assert.Contains("certs/ca.pem", scrubbed, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(scrubbed);  // still decodable
        Assert.Equal("cannot open certs/ca.pem", doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// The property <c>SecurityDiagnosticPathDisclosureTests</c> asserts, restated over the same
    /// token predicate: no rooted token survives anywhere in the rendered text.
    /// </summary>
    /// <remarks>
    /// A property rather than an expected string, for the reason that file records: an expected
    /// string passes for a sibling channel leaking some OTHER host path, which is the failure
    /// this class is guarding against reintroducing.
    /// </remarks>
    private static void AssertNoAbsoluteHostPath(string channel, string text, string suiteDirectory)
    {
        Assert.DoesNotContain(suiteDirectory, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            JsonEncodedText.Encode(suiteDirectory).ToString(), text, StringComparison.OrdinalIgnoreCase);

        foreach (var token in text.Split(s_tokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.TrimEnd(s_trailingPunctuation);
            if (candidate.Length < 2 || candidate.IndexOfAny(s_pathSeparators) < 0)
            {
                continue;
            }

            Assert.False(
                Path.IsPathRooted(candidate),
                $"{channel} names an absolute host path '{candidate}'. A diagnostic that reaches "
                + "an archived channel must name the declared path (#357's rule, extended to "
                + "third-party client text by issue #375), never a resolved one. Full text: "
                + text);
        }
    }
}
