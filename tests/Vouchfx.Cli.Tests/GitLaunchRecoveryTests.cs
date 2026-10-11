using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class GitLaunchRecoveryTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "git-recovery-repo"));
    private static string Candidate(int index) => Path.Combine(Root, "bin" + index, "git");
    private static ProcessLaunchException LoadFailure() => new(
        "PRIVATE_EXECUTABLE_PATH", new Win32Exception(216), ProcessLaunchFailureKind.ExecutableLoad);

    private sealed class Runner : IProcessRunner
    {
        public Func<string, IReadOnlyList<string>, ProcessResult>? OnRun { get; set; }
        public List<(string Path, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Environment, CancellationToken Token)> Calls { get; } = new();

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, environment, cancellationToken));
            return OnRun?.Invoke(fileName, arguments) ?? Success(arguments);
        }
    }

    private static ProcessResult Success(IReadOnlyList<string> arguments) =>
        new(0, arguments.Contains("rev-parse") ? Root + "\n" : arguments.Contains("status") ? "?? pass.e2e.yaml\n" : string.Empty, string.Empty);

    private static GitChangeSet Create(Runner runner, IReadOnlyList<string> candidates, CancellationToken token = default) =>
        new("HEAD", Root, runner, null, () => candidates, token);

    [Fact]
    public void LoadFailure_ContinuesToNextCandidate_ThenPinsEveryOperation()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner { OnRun = (path, args) => path == Candidate(0) ? throw LoadFailure() : Success(args) };
        var changes = Create(runner, new[] { Candidate(0), Candidate(1), Candidate(2) }, cancellation.Token);

        Assert.True(changes.IsChanged(Path.Combine(Root, "pass.e2e.yaml")));
        Assert.Equal(new[] { Candidate(0), Candidate(1), Candidate(1), Candidate(1) }, runner.Calls.Select(call => call.Path));
        Assert.All(runner.Calls, call =>
        {
            Assert.Contains("--no-optional-locks", call.Args);
            Assert.Same(runner.Calls[0].Environment, call.Environment);
            Assert.Equal(cancellation.Token, call.Token);
        });
        Assert.DoesNotContain(runner.Calls, call => call.Args.Contains("--version"));
    }

    [Theory]
    [InlineData("unclassified-launch")]
    [InlineData("timeout")]
    [InlineData("capture")]
    [InlineData("child-nonzero")]
    [InlineData("empty-root")]
    public void OtherFailure_NeverTriesAnotherCandidate(string kind)
    {
        var runner = new Runner
        {
            OnRun = (_, _) => kind switch
            {
                "unclassified-launch" => throw new ProcessLaunchException("refused", new Win32Exception(216)),
                "timeout" => throw new ProcessTimeoutException("timeout", TimeSpan.FromSeconds(1)),
                "capture" => throw new ProcessCaptureException("capture", new IOException("read refused")),
                "child-nonzero" => new ProcessResult(128, string.Empty, "real git refused"),
                _ => new ProcessResult(0, string.Empty, string.Empty),
            },
        };
        var error = Assert.Throws<ChangeSetException>(() => Create(runner, new[] { Candidate(0), Candidate(1) }));
        Assert.Single(runner.Calls);
        Assert.NotEqual(GitChangeSet.GitCandidatesExhausted, error.Message);
        if (kind == "child-nonzero")
        {
            Assert.Contains("exit 128", error.Message, StringComparison.Ordinal);
            Assert.Contains("real git refused", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("status")]
    public void ChosenExecutable_LaterLoadFailure_DoesNotSwitchBinary(string verb)
    {
        var failure = LoadFailure();
        var runner = new Runner { OnRun = (_, args) => args.Contains(verb) ? throw failure : Success(args) };
        var error = Assert.Throws<ChangeSetException>(() => Create(runner, new[] { Candidate(0), Candidate(1) }));
        Assert.Same(failure, error.InnerException);
        Assert.All(runner.Calls, call => Assert.Equal(Candidate(0), call.Path));
        Assert.Equal(verb == "diff" ? 2 : 3, runner.Calls.Count);
        Assert.DoesNotContain("PRIVATE_EXECUTABLE_PATH", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllLoadFailures_RefuseWithFixedDiagnostic_AndRetainCause()
    {
        var failure = LoadFailure();
        var runner = new Runner { OnRun = (_, _) => throw failure };
        var error = Assert.Throws<ChangeSetException>(() => Create(runner, new[] { Candidate(0), Candidate(1) }));
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(GitChangeSet.GitCandidatesExhausted, error.Message);
        Assert.Same(failure, error.InnerException);
        Assert.DoesNotContain(Root, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_EXECUTABLE_PATH", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAttempts_AreBounded_EvenForAnInjectedLongCandidateList()
    {
        var runner = new Runner { OnRun = (_, _) => throw LoadFailure() };
        var candidates = Enumerable.Range(0, GitChangeSet.MaximumGitLaunchAttempts + 10).Select(Candidate).ToArray();
        Assert.Throws<ChangeSetException>(() => Create(runner, candidates));
        Assert.Equal(32, GitChangeSet.MaximumGitLaunchAttempts);
        Assert.Equal(GitChangeSet.MaximumGitLaunchAttempts, runner.Calls.Count);
    }

    [Fact]
    public void CandidateEntries_AreBounded_EvenWhenNoneAreAbsolute()
    {
        var runner = new Runner();
        var candidates = Enumerable.Repeat("relative/git", GitChangeSet.MaximumPathEntries).Append(Candidate(0)).ToArray();
        Assert.Throws<ChangeSetException>(() => Create(runner, candidates));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void DuplicateCandidatePaths_AreOnlyAttemptedOnce()
    {
        var runner = new Runner { OnRun = (path, args) => path == Candidate(0) ? throw LoadFailure() : Success(args) };
        Create(runner, new[] { Candidate(0), Candidate(0), Path.Combine(Root, "bin0", "..", "bin0", "git"), Candidate(1) });
        Assert.Equal(new[] { Candidate(0), Candidate(1), Candidate(1), Candidate(1) }, runner.Calls.Select(call => call.Path));
    }

    [Fact]
    public void CancellationBetweenCandidates_Propagates_WithoutAnotherLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner { OnRun = (_, _) => { cancellation.Cancel(); throw LoadFailure(); } };
        var error = Assert.Throws<OperationCanceledException>(() => Create(runner, new[] { Candidate(0), Candidate(1) }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void AlreadyCancelledCaller_DoesNotLaunch_EvenWithAnIgnoringRunner()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new Runner();
        Assert.Throws<OperationCanceledException>(() => Create(runner, new[] { Candidate(0) }, cancellation.Token));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void InjectedSingleLocator_RetainsItsExistingRefusal_AndDoesNotConsultCandidates()
    {
        var failure = LoadFailure();
        var runner = new Runner { OnRun = (_, _) => throw failure };
        var error = Assert.Throws<ChangeSetException>(() => new GitChangeSet("HEAD", Root, runner,
            () => Candidate(0), () => throw new InvalidOperationException("must not consult"), CancellationToken.None));
        Assert.Single(runner.Calls);
        Assert.Same(failure, error.InnerException);
        Assert.Contains("Is the selected git on PATH", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 193, true)]
    [InlineData(true, 216, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 3, false)]
    [InlineData(true, 5, false)]
    [InlineData(true, 8, false)]
    [InlineData(true, 267, false)]
    [InlineData(false, 8, true)]
    [InlineData(false, 2, true)]
    [InlineData(false, 13, false)]
    [InlineData(false, 12, false)]
    [InlineData(false, 22, false)]
    [InlineData(false, 193, false)]
    public void BoundaryClassification_UsesOnlyTypedPlatformCodes(bool windows, int code, bool expected)
    {
        var actual = SystemProcessRunner.ClassifyLaunchFailure(new Win32Exception(code, "unrelated localised wording"), windows, Path.GetTempPath());
        Assert.Equal(expected ? ProcessLaunchFailureKind.ExecutableLoad : ProcessLaunchFailureKind.Unclassified, actual);
    }

    [Fact]
    public void MissingWorkingDirectory_AndNonNativeException_AreNotLoadFailures()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Equal(ProcessLaunchFailureKind.Unclassified,
            SystemProcessRunner.ClassifyLaunchFailure(new Win32Exception(2), windows: false, missing));
        Assert.Equal(ProcessLaunchFailureKind.Unclassified,
            SystemProcessRunner.ClassifyLaunchFailure(new IOException("Exec format error"), windows: false, Path.GetTempPath()));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Directory.CreateTempSubdirectory("git-candidates-").FullName;
            ExecutablePath = Path.Combine(DirectoryPath, GitChangeSet.CandidateFileName("git", OperatingSystem.IsWindows()));
            File.WriteAllText(ExecutablePath, string.Empty);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string DirectoryPath { get; }
        public string ExecutablePath { get; }
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    [Fact]
    public void PathCandidates_PreserveOrder_Deduplicate_AndIgnoreRelativeEmptyEntries()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var path = string.Join(Path.PathSeparator, "", ".", "relative", first.DirectoryPath,
            first.DirectoryPath, second.DirectoryPath, "");
        Assert.Equal(new[] { first.ExecutablePath, second.ExecutablePath }, GitChangeSet.LocateCandidatesOnPath("git", path));
        Assert.Empty(GitChangeSet.LocateCandidatesOnPath("git", null));
        Assert.Empty(GitChangeSet.LocateCandidatesOnPath("git", string.Empty));
    }

    [Fact]
    public void PathCandidates_DoNotTrimOrUnquoteEntries()
    {
        using var fixture = new Fixture();
        var path = string.Join(Path.PathSeparator, " " + fixture.DirectoryPath, fixture.DirectoryPath + " ",
            "\"" + fixture.DirectoryPath + "\"", fixture.DirectoryPath);
        Assert.Equal(new[] { fixture.ExecutablePath }, GitChangeSet.LocateCandidatesOnPath("git", path));
    }

    [Fact]
    public void PathEntryLimit_IsFixed_AndDoesNotSearchTheFollowingEntry()
    {
        using var fixture = new Fixture();
        var path = string.Join(Path.PathSeparator, Enumerable.Repeat("relative", GitChangeSet.MaximumPathEntries).Append(fixture.DirectoryPath));
        Assert.Equal(220, GitChangeSet.MaximumPathEntries);
        Assert.Empty(GitChangeSet.LocateCandidatesOnPath("git", path));
        var within = string.Join(Path.PathSeparator, Enumerable.Repeat("relative", GitChangeSet.MaximumPathEntries - 1).Append(fixture.DirectoryPath));
        Assert.Equal(new[] { fixture.ExecutablePath }, GitChangeSet.LocateCandidatesOnPath("git", within));
    }

    [Fact]
    public void PathCharacterLimit_DoesNotInterpretAPartialEntryAsAnotherDirectory()
    {
        using var fixture = new Fixture();
        var path = new string('x', GitChangeSet.MaximumPathCharacters + 1) + Path.PathSeparator + fixture.DirectoryPath;
        Assert.Equal(32_768, GitChangeSet.MaximumPathCharacters);
        Assert.Empty(GitChangeSet.LocateCandidatesOnPath("git", path));
    }

    [Fact]
    public void PathCandidates_SkipMalformedEntries_BeforeNormalisingPaths()
    {
        using var fixture = new Fixture();
        var malformed = fixture.DirectoryPath + "\0";
        var path = malformed + Path.PathSeparator + fixture.DirectoryPath;
        Assert.Equal(new[] { fixture.ExecutablePath }, GitChangeSet.LocateCandidatesOnPath("git", path));
    }

    [Fact]
    public void PathCharacterBudget_AcceptsACompleteLastEntry_ButDoesNotReadTheNextCharacter()
    {
        using var fixture = new Fixture();
        var prefix = new string('x', GitChangeSet.MaximumPathCharacters - fixture.DirectoryPath.Length - 1);
        var within = prefix + Path.PathSeparator + fixture.DirectoryPath;
        Assert.Equal(GitChangeSet.MaximumPathCharacters, within.Length);
        Assert.Equal(new[] { fixture.ExecutablePath }, GitChangeSet.LocateCandidatesOnPath("git", within));
        Assert.Empty(GitChangeSet.LocateCandidatesOnPath("git", within + Path.PathSeparator + fixture.DirectoryPath));
    }
}
