// Vouchfx.Cli.Tests — telemetry CLI wiring (S10-G-04). No Docker.
//
// Covers the CLI-side telemetry surface that does NOT need a topology:
//   • the `telemetry` command exposes enable/disable/status subcommands;
//   • `run` parses --no-telemetry into the right bool;
//   • TelemetryRunHook captures the buffered event stream into a temp file path,
//     reads it back, builds the allowlisted event, and appends it to the (injected)
//     sink — and suppresses all of that when consent is not Enabled or the run is
//     opted out.  Every path uses an injected temp directory; the real %APPDATA% is
//     never touched.

using System.CommandLine;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.Cli;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Telemetry;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class TelemetryCliTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TelemetryCommand_ExposesEnableDisableStatusSubcommands()
    {
        var command = TelemetryCommand.Build();

        var names = command.Subcommands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "disable", "enable", "status" }, names);
    }

    [Fact]
    public void RunCommand_NoTelemetryOption_ParsesToTrueWhenPresent()
    {
        var option = RunCommand.BuildNoTelemetryOption();
        var command = new Command("run");
        command.Add(option);

        Assert.True(command.Parse(new[] { "--no-telemetry" }).GetValue(option));
        Assert.False(command.Parse(Array.Empty<string>()).GetValue(option));
    }

    [Fact]
    public void RootRun_AcceptsNoTelemetryAlongsideOtherFlags()
    {
        var command = RunCommand.Build();
        var result = command.Parse(new[] { "scenarios", "--no-telemetry", "--tag", "smoke" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Hook_Emits_WhenEnabled_FromCapturedEventsFile()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var sink = new RecordingSinkCli();

        var hook = new TelemetryRunHook(store, temp, sink, noTelemetryFlag: false, TextWriter.Null);

        // No user --events path → the hook resolves a private temp capture path.
        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.NotNull(eventsPath);
        Assert.True(isTemp);

        // Simulate the runner writing the verbatim buffered stream to that path.
        await File.WriteAllLinesAsync(eventsPath!, SyntheticStream());

        await hook.EmitAsync(eventsPath, isTemp, T0, CancellationToken.None);

        Assert.Single(sink.Sent);
        Assert.Equal(1, sink.Sent[0].ScenarioCount);
        Assert.Equal(1, sink.Sent[0].StepProviders["http.rest"]);
        // The hook deletes the temp capture file after reading.
        Assert.False(File.Exists(eventsPath));
    }

    [Fact]
    public async Task Hook_EmitAsync_ThreadsRunStartedAtThrough_ToBuiltEvents_StartupMs()
    {
        // #568: pins hook → builder plumbing — the runStartedAt EmitAsync is given is the
        // SAME anchor TelemetryEventBuilder.Build measures startupMs from. A stream whose
        // first scenario-started sits 250ms after the supplied anchor must produce
        // StartupMs == 250.
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var sink = new RecordingSinkCli();

        var hook = new TelemetryRunHook(store, temp, sink, noTelemetryFlag: false, TextWriter.Null);

        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.NotNull(eventsPath);

        var stream = new[]
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = "run0", Timestamp = T0.AddMilliseconds(250), ScenarioId = "A",
            }),
            EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = "run0",
                Timestamp = T0.AddMilliseconds(260),
                ScenarioId = "A",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };
        await File.WriteAllLinesAsync(eventsPath!, stream);

        await hook.EmitAsync(eventsPath, isTemp, runStartedAt: T0, CancellationToken.None);

        Assert.Single(sink.Sent);
        Assert.Equal(250, sink.Sent[0].StartupMs);
    }

    [Theory]
    [InlineData(false)] // Undecided: a fresh store.
    [InlineData(true)]  // Explicitly disabled.
    public async Task Hook_DoesNotEmit_WhenConsentNotEnabled(bool disabled)
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        if (disabled)
        {
            store.Disable();
        }

        var sink = new RecordingSinkCli();
        var hook = new TelemetryRunHook(store, temp, sink, noTelemetryFlag: false, TextWriter.Null);

        // When not emitting, the hook reuses the user's (null) path and never asks for a temp file.
        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.Null(eventsPath);
        Assert.False(isTemp);

        // Even if a stream existed, EmitAsync with a null path is a no-op.
        await hook.EmitAsync(eventsPath, isTemp, T0, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public async Task Hook_DoesNotEmit_WhenEnabledButNoTelemetryFlagSet()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var sink = new RecordingSinkCli();

        var hook = new TelemetryRunHook(store, temp, sink, noTelemetryFlag: true, TextWriter.Null);

        var (eventsPath, _) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.Null(eventsPath);
        await hook.EmitAsync(eventsPath, isTempFile: false, T0, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void Hook_ReusesUserEventsPath_WhenProvided_NoTempFile()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var hook = new TelemetryRunHook(
            store, temp, new RecordingSinkCli(), noTelemetryFlag: false, TextWriter.Null);

        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: "user-report.jsonl");

        // The user's own --events path is reused verbatim; no temp file is created/owned.
        Assert.Equal("user-report.jsonl", eventsPath);
        Assert.False(isTemp);
    }

    [Fact]
    public async Task Disable_FiresForgetWithInstallId_ThenLocalOptOutAlwaysRuns()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        var enabled = store.Enable();          // mints an install id + sets Enabled
        Assert.NotNull(enabled.InstallId);

        Guid? forgottenId = null;
        var output = new StringWriter();

        await TelemetryCommand.DisableAsync(
            store,
            id => { forgottenId = id; return Task.CompletedTask; },
            output,
            CancellationToken.None);

        // The forget fired FIRST, with the still-present install id...
        Assert.Equal(enabled.InstallId, forgottenId);
        // ...and the local opt-out ran: consent is Disabled and the install id is gone.
        var after = store.Read();
        Assert.Equal(TelemetryConsent.Disabled, after.Consent);
        Assert.Null(after.InstallId);
        Assert.Contains("DISABLED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_LocalOptOutStillRuns_WhenForgetFaults()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var output = new StringWriter();

        // The forget faults (e.g. an unexpected throw escaping the best-effort wrapper).  The
        // local opt-out MUST still run regardless — disabling is a privacy guarantee, not
        // best-effort — whether or not the fault surfaces afterwards (it runs in a finally).
        await Record.ExceptionAsync(() => TelemetryCommand.DisableAsync(
            store,
            _ => throw new InvalidOperationException("forget blew up"),
            output,
            CancellationToken.None));

        var after = store.Read();
        Assert.Equal(TelemetryConsent.Disabled, after.Consent);
        Assert.Null(after.InstallId);
        Assert.Contains("DISABLED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_ReportsSuccessAndOptsOut_WhenAlreadyCancelled()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        // Seed an outbox so we can prove it is cleared by the local opt-out.
        File.WriteAllText(temp.OutboxPath, "{\"k\":1}\n");
        var output = new StringWriter();

        // The token is ALREADY cancelled at entry (e.g. a Ctrl-C before the command even
        // begins).  `telemetry disable` is a privacy command whose local opt-out always
        // succeeds, so it must NOT abort: there is no entry-guard throw, and the best-effort
        // forget observes cancellation cooperatively (production swallows OperationCanceled),
        // so DisableAsync runs to completion and the wired action returns ExitCodes.Success.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var forgetSawCancellation = false;
        var ex = await Record.ExceptionAsync(() => TelemetryCommand.DisableAsync(
            store,
            _ =>
            {
                // Mirror the production forget wrapper: notice the cancel but stay fail-silent
                // (it never lets an OperationCanceledException escape the disable path).
                forgetSawCancellation = cts.IsCancellationRequested;
                return Task.CompletedTask;
            },
            output,
            cts.Token));

        // No throw: DisableAsync completed, so the action's `return ExitCodes.Success` is reached.
        Assert.Null(ex);
        Assert.True(forgetSawCancellation);

        // The local opt-out ran: consent is Disabled, the install id is gone, the outbox cleared.
        var after = store.Read();
        Assert.Equal(TelemetryConsent.Disabled, after.Consent);
        Assert.Null(after.InstallId);
        Assert.False(File.Exists(temp.OutboxPath));
        Assert.Contains("DISABLED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hook_EnvironmentConfigured_EmitsWithSuppliedId_WithoutStoreConsent()
    {
        using var temp = new TempPathsCli();
        // A FRESH store: consent Undecided, no install id — env-configured mode must
        // not need it (this is the CI path on an ephemeral runner).
        var store = new TelemetryConsentStore(temp);
        var sink = new RecordingSinkCli();
        var suppliedId = Guid.Parse("8f7c2f0a-1b2c-4d3e-9f4a-5b6c7d8e9f0a");

        var hook = new TelemetryRunHook(
            store, temp, sink, noTelemetryFlag: false, TextWriter.Null,
            environmentInstallIdRaw: suppliedId.ToString(),
            transportConfigured: true);

        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.NotNull(eventsPath);
        await File.WriteAllLinesAsync(eventsPath!, SyntheticStream());
        await hook.EmitAsync(eventsPath, isTemp, T0, CancellationToken.None);

        // The event carries the SUPPLIED id (stable per repo), and nothing was
        // written to the local consent store — env mode bypasses it entirely.
        Assert.Single(sink.Sent);
        Assert.Equal(suppliedId, sink.Sent[0].InstallId);
        Assert.False(File.Exists(temp.ConsentStorePath));
    }

    [Fact]
    public async Task Hook_EnvironmentId_WithoutConfiguredTransport_FallsBackToStore()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);   // Undecided
        var sink = new RecordingSinkCli();

        // Install id supplied but endpoint/token NOT configured → not env mode; the
        // store (Undecided) governs, so nothing is collected or emitted.
        var hook = new TelemetryRunHook(
            store, temp, sink, noTelemetryFlag: false, TextWriter.Null,
            environmentInstallIdRaw: Guid.NewGuid().ToString(),
            transportConfigured: false);

        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.Null(eventsPath);
        await hook.EmitAsync(eventsPath, isTemp, T0, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public async Task Hook_EnvironmentConfigured_StillSuppressedByNoTelemetryFlag()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        var sink = new RecordingSinkCli();

        // The suppression conjunction keeps absolute precedence over env-configured
        // opt-in (--no-telemetry here; VOUCHFX_NO_TELEMETRY goes through the same gate).
        var hook = new TelemetryRunHook(
            store, temp, sink, noTelemetryFlag: true, TextWriter.Null,
            environmentInstallIdRaw: Guid.NewGuid().ToString(),
            transportConfigured: true);

        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.Null(eventsPath);
        await hook.EmitAsync(eventsPath, isTemp, T0, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public async Task Hook_InvalidEnvironmentId_WarnsOnce_AndFallsBackToStore()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        var enabled = store.Enable();                  // local consent with a store id
        var sink = new RecordingSinkCli();
        var diagnostics = new StringWriter();

        var hook = new TelemetryRunHook(
            store, temp, sink, noTelemetryFlag: false, diagnostics,
            environmentInstallIdRaw: "not-a-guid",
            transportConfigured: true);

        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.NotNull(eventsPath);
        await File.WriteAllLinesAsync(eventsPath!, SyntheticStream());
        await hook.EmitAsync(eventsPath, isTemp, T0, CancellationToken.None);

        // The invalid env id was ignored with a single warning naming the variable
        // (never echoing the raw value into an event), and the STORE id was used.
        Assert.Single(sink.Sent);
        Assert.Equal(enabled.InstallId, sink.Sent[0].InstallId);
        var warning = diagnostics.ToString();
        Assert.Contains(TelemetryInstallId.EnvVar, warning, StringComparison.Ordinal);
        Assert.Equal(1, warning.Split(TelemetryInstallId.EnvVar).Length - 1);
    }

    [Fact]
    public void Hook_EnvironmentConfigured_SuppressesFirstRunNotice()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);   // Undecided → notice would show
        var hook = new TelemetryRunHook(
            store, temp, new RecordingSinkCli(), noTelemetryFlag: false, TextWriter.Null,
            environmentInstallIdRaw: Guid.NewGuid().ToString(),
            transportConfigured: true);

        // Env-configured telemetry is active: the store-based "undecided" notice would
        // be misleading and would nag on every ephemeral CI run — it must not appear.
        var stderr = new StringWriter();
        hook.MaybeShowFirstRunNotice(stderr);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Hook_FirstRunNotice_ShownOnceWhenUndecided()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        var hook = new TelemetryRunHook(
            store, temp, new RecordingSinkCli(), noTelemetryFlag: false, TextWriter.Null);

        var first = new StringWriter();
        hook.MaybeShowFirstRunNotice(first);
        var second = new StringWriter();
        hook.MaybeShowFirstRunNotice(second);

        Assert.Contains("telemetry", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, second.ToString());
    }

    /// <summary>
    /// A SOURCE CENSUS over <c>RunCommand.cs</c> (#568 gate review), following the
    /// precedent set by <c>ShutdownBackstopTests.StdinEofCallback_ArmsTheBackstop_BeforeCancellingTheLinkedSource</c>:
    /// the capture-placement guarantee is a property of <c>ExecuteRunPipelineAsync</c>'s STATEMENT
    /// ORDER, not of any behaviour <see cref="TelemetryRunHook"/> or <see cref="TelemetryEventBuilder"/>
    /// can observe on their own — every row above (and every row in
    /// Vouchfx.Engine.Telemetry.Tests) would stay green whether <c>runStartedAt</c> is captured at
    /// the top of the method or moved to just before <c>EmitAsync</c>, because both hooks/builders
    /// behave correctly for whatever instant they are GIVEN.  It is the CALLER's placement of that
    /// capture that is the guarantee, and a behavioural probe for it needs a real scenario to
    /// execute (no non-Docker test in this project can produce one — see this project's own csproj
    /// comment) or depends on a pre-existing, separately-tracked bug (telemetry reading a stale
    /// <c>--events</c> file when nothing parses), so this is pinned by reading the source instead.
    /// </summary>
    /// <remarks>
    /// VACUITY FIRST: the single <c>ExecuteRunPipelineAsync</c> method declaration and the single
    /// <c>EmitAsync</c> call within it are asserted to exist before anything is concluded from
    /// their shape — a census that stops finding its needle reports no offence and passes for free.
    /// </remarks>
    [Fact]
    public void ExecuteRunPipelineAsync_CapturesRunStartedAtFirst_NeverReassigns_AndPassesItToEmitAsync()
    {
        var method = ExecuteRunPipelineAsyncMethod();
        var body = method.Body;
        Assert.True(
            body is not null,
            "ExecuteRunPipelineAsync no longer has a block body; this census cannot read its "
            + "statement order.");

        // (1) The FIRST statement is `var runStartedAt = DateTimeOffset.UtcNow;` — nothing may
        // run before it (discovery, the --watch/--parallel guard, anything). #568: EmitAsync
        // runs only after both runners (sequential/parallel) return, so a capture moved to
        // just before it is later than every archived line - it clamps startupMs AND
        // timeToFirstTestMs to 0 on every run - the original #568 bug.
        var firstStatement = body!.Statements.FirstOrDefault();
        var localDecl = firstStatement as LocalDeclarationStatementSyntax;
        Assert.True(
            localDecl is not null,
            "ExecuteRunPipelineAsync's FIRST statement is no longer a local declaration (found: "
            + $"'{firstStatement}'). #568: runStartedAt must be captured before ANYTHING else in "
            + "this method or startupMs stops measuring the run's true start.");

        var variables = localDecl!.Declaration.Variables;
        Assert.True(
            variables.Count == 1 && variables[0].Identifier.ValueText == "runStartedAt",
            "ExecuteRunPipelineAsync's first statement no longer declares exactly one variable "
            + "named 'runStartedAt' (found: "
            + $"{string.Join(", ", variables.Select(v => v.Identifier.ValueText))}). This is the "
            + "#568 anchor; renaming or relocating it away from the first statement breaks the "
            + "telemetry startupMs contract silently.");

        var initializer = variables[0].Initializer?.Value;
        var isUtcNow = initializer is MemberAccessExpressionSyntax
        {
            Name.Identifier.ValueText: "UtcNow",
            Expression: IdentifierNameSyntax { Identifier.ValueText: "DateTimeOffset" },
        };
        Assert.True(
            isUtcNow,
            "runStartedAt's initializer is no longer DateTimeOffset.UtcNow (found: "
            + $"'{initializer}'). Every event on the buffered stream is stamped from "
            + "DateTimeOffset.UtcNow too (§14); capturing the anchor from a different clock "
            + "corrupts the startupMs/timeToFirstTestMs comparison.");

        // (2) Nothing in the method reassigns runStartedAt after its initial declaration.
        var reassignments = method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "runStartedAt" })
            .ToList();
        Assert.True(
            reassignments.Count == 0,
            $"ExecuteRunPipelineAsync reassigns runStartedAt {reassignments.Count} time(s) after "
            + "its initial declaration. #568's fix depends on this value staying fixed at the "
            + "pipeline's very first instant; any later reassignment (e.g. re-captured just "
            + "before EmitAsync) silently reproduces the original bug under a different guise.");

        // (3) Exactly one EmitAsync call, and its THIRD argument (index 2) is the identifier
        // runStartedAt — not a fresh DateTimeOffset.UtcNow captured at the call site, and not
        // some other value.
        var emitCalls = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "EmitAsync" })
            .ToList();
        Assert.True(
            emitCalls.Count == 1,
            $"Expected exactly 1 EmitAsync call in ExecuteRunPipelineAsync, found "
            + $"{emitCalls.Count}. Zero means this census stopped matching and guards nothing; "
            + "more than one means a second call site could pass a different (or missing) "
            + "runStartedAt undetected.");

        var args = emitCalls[0].ArgumentList.Arguments;
        Assert.True(
            args.Count > 2,
            $"The EmitAsync call has only {args.Count} argument(s); the third (index 2, "
            + "runStartedAt) is missing entirely.");

        var thirdArgIdentifier = args[2].Expression as IdentifierNameSyntax;
        Assert.True(
            thirdArgIdentifier?.Identifier.ValueText == "runStartedAt",
            "EmitAsync's third argument (index 2) is no longer the identifier 'runStartedAt' "
            + $"(found: '{args[2].Expression}'). #568: EmitAsync runs only after both runners "
            + "return, so passing DateTimeOffset.UtcNow (or anything else) captured HERE instead "
            + "of the value captured at the top of the method clamps startupMs and "
            + "timeToFirstTestMs to 0 on every run - the original #568 bug, returned via a "
            + "different capture site.");
    }

    /// <summary>The single <c>ExecuteRunPipelineAsync</c> method declaration in RunCommand.cs.</summary>
    private static MethodDeclarationSyntax ExecuteRunPipelineAsyncMethod()
    {
        var methods = ParsedRunCommand()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "ExecuteRunPipelineAsync")
            .ToList();

        Assert.True(
            methods.Count == 1,
            "Expected exactly 1 ExecuteRunPipelineAsync method declaration in RunCommand.cs, "
            + $"found {methods.Count}. Zero means this census stopped matching and guards "
            + "nothing.");

        return methods[0];
    }

    /// <summary>The parsed syntax tree of the CLI's <c>RunCommand.cs</c>.</summary>
    private static CompilationUnitSyntax ParsedRunCommand()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var path = Path.Combine(
            dir!.FullName, "src", "Cli", "Vouchfx.Cli", "RunCommand.cs");

        Assert.True(File.Exists(path), $"'{path}' not found; this census cannot run.");

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            .GetCompilationUnitRoot();
    }

    private static string[] SyntheticStream() => new[]
    {
        EventStreamJson.ToLine(new ScenarioStartedEvent
        {
            RunId = "run0", Timestamp = T0, ScenarioId = "A",
        }),
        EventStreamJson.ToLine(new StepStartedEvent
        {
            RunId = "run0", Timestamp = T0.AddMilliseconds(10), StepId = "s1", Kind = "http.rest",
        }),
        EventStreamJson.ToLine(new ScenarioCompletedEvent
        {
            RunId = "run0",
            Timestamp = T0.AddMilliseconds(20),
            ScenarioId = "A",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),
    };

    /// <summary>A temp-dir-rooted <see cref="ITelemetryPaths"/> for the CLI tests.</summary>
    private sealed class TempPathsCli : ITelemetryPaths, IDisposable
    {
        private readonly string _baseDir;
        private readonly DefaultTelemetryPaths _inner;

        public TempPathsCli()
        {
            _baseDir = Path.Combine(
                Path.GetTempPath(), "vouchfx-cli-telemetry-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_baseDir);
            _inner = new DefaultTelemetryPaths(_baseDir);
        }

        public string ConsentStorePath => _inner.ConsentStorePath;

        public string OutboxPath => _inner.OutboxPath;

        public string DrainStatePath => _inner.DrainStatePath;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_baseDir))
                {
                    Directory.Delete(_baseDir, recursive: true);
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

    /// <summary>An in-memory sink that records what it was sent.</summary>
    private sealed class RecordingSinkCli : ITelemetrySink
    {
        public List<TelemetryEvent> Sent { get; } = new();

        public Task SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            Sent.Add(telemetryEvent);
            return Task.CompletedTask;
        }
    }
}
