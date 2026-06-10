// Tests for the engine-owned RETRY runner (§7 verifyMode RETRY, §12.1 taxonomy).
// Written RED-first before the implementation exists.
//
// Tests pass a small pollIntervalMs (10) to keep them fast and never assert exact
// wall-clock durations — CI is loaded and timing is non-deterministic.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Retry;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Retry;

/// <summary>
/// Verifies the behaviour contract of <see cref="RetryRunner.PollAsync"/>: the
/// per-attempt record, the terminal verdicts (Pass, EnvironmentError, throw), the
/// timeout-to-Inconclusive resolution (never Fail), and the two written Vars keys.
/// </summary>
public sealed class RetryRunnerTests
{
    private const string OutcomeKey = "__outcome::step_under_test";
    private const string AttemptsKey = "__attempts::step_under_test";

    // -------------------------------------------------------------------------
    // Pass after a couple of failing attempts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PollAsync_PassesOnThirdAttempt()
    {
        // Arrange — Fail, Fail, then Pass.
        var vars = new Dictionary<string, object?>();
        var calls = 0;

        Task<StepOutcome> Attempt(CancellationToken _)
        {
            calls++;
            var verdict = calls < 3 ? Verdict.Fail : Verdict.Pass;
            return Task.FromResult(new StepOutcome(verdict, 1L, null));
        }

        // Act
        await RetryRunner.PollAsync(
            vars,
            OutcomeKey,
            AttemptsKey,
            timeoutMs: 5_000,
            pollIntervalMs: 10,
            Attempt);

        // Assert — final outcome is Pass.
        var outcome = Assert.IsType<StepOutcome>(vars[OutcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);

        // Assert — exactly three records, 1-based and in order, last one Pass.
        var attempts = Assert.IsType<List<AttemptRecord>>(vars[AttemptsKey]);
        Assert.Equal(3, attempts.Count);
        Assert.Equal(1, attempts[0].Attempt);
        Assert.Equal(2, attempts[1].Attempt);
        Assert.Equal(3, attempts[2].Attempt);
        Assert.Equal(Verdict.Pass, attempts[^1].Verdict);
    }

    // -------------------------------------------------------------------------
    // Never satisfied → timeout → Inconclusive (never Fail)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PollAsync_NeverSatisfiedTimesOutInconclusive()
    {
        // Arrange — always Fail.
        var vars = new Dictionary<string, object?>();

        Task<StepOutcome> Attempt(CancellationToken _)
            => Task.FromResult(new StepOutcome(Verdict.Fail, 1L, null));

        // Act — tight window so the test finishes quickly.
        await RetryRunner.PollAsync(
            vars,
            OutcomeKey,
            AttemptsKey,
            timeoutMs: 200,
            pollIntervalMs: 10,
            Attempt);

        // Assert — Inconclusive, never Fail.
        var outcome = Assert.IsType<StepOutcome>(vars[OutcomeKey]);
        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.NotEqual(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("retry-timeout", outcome.Observation);

        // Assert — at least one attempt was recorded.
        var attempts = Assert.IsType<List<AttemptRecord>>(vars[AttemptsKey]);
        Assert.True(attempts.Count >= 1);
    }

    // -------------------------------------------------------------------------
    // EnvironmentError is terminal — stop immediately, do not retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PollAsync_EnvironmentErrorIsTerminal()
    {
        // Arrange — first call returns EnvironmentError; a second call would return
        // Pass, but the runner must never make it.
        var vars = new Dictionary<string, object?>();
        var calls = 0;

        Task<StepOutcome> Attempt(CancellationToken _)
        {
            calls++;
            var verdict = calls == 1 ? Verdict.EnvironmentError : Verdict.Pass;
            return Task.FromResult(new StepOutcome(verdict, 1L, "broker down"));
        }

        // Act
        await RetryRunner.PollAsync(
            vars,
            OutcomeKey,
            AttemptsKey,
            timeoutMs: 5_000,
            pollIntervalMs: 10,
            Attempt);

        // Assert — terminal EnvironmentError, exactly one attempt.
        var outcome = Assert.IsType<StepOutcome>(vars[OutcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.Equal(1, calls);

        var attempts = Assert.IsType<List<AttemptRecord>>(vars[AttemptsKey]);
        Assert.Single(attempts);
        Assert.Equal(Verdict.EnvironmentError, attempts[0].Verdict);
    }

    // -------------------------------------------------------------------------
    // A thrown exception is a terminal EnvironmentError
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PollAsync_AttemptThrowIsTerminalEnvironmentError()
    {
        // Arrange — the attempt throws.
        var vars = new Dictionary<string, object?>();

        Task<StepOutcome> Attempt(CancellationToken _)
            => throw new InvalidOperationException("connection refused");

        // Act
        await RetryRunner.PollAsync(
            vars,
            OutcomeKey,
            AttemptsKey,
            timeoutMs: 5_000,
            pollIntervalMs: 10,
            Attempt);

        // Assert — terminal EnvironmentError, one record with a non-null observation.
        var outcome = Assert.IsType<StepOutcome>(vars[OutcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);

        var attempts = Assert.IsType<List<AttemptRecord>>(vars[AttemptsKey]);
        Assert.Single(attempts);
        Assert.Equal(Verdict.EnvironmentError, attempts[0].Verdict);
        Assert.NotNull(attempts[0].Observation);
    }

    // -------------------------------------------------------------------------
    // Both Vars keys are always populated
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PollAsync_WritesBothVarsKeys()
    {
        // Arrange — a single Pass on the first attempt is enough.
        var vars = new Dictionary<string, object?>();

        Task<StepOutcome> Attempt(CancellationToken _)
            => Task.FromResult(new StepOutcome(Verdict.Pass, 1L, null));

        // Act
        await RetryRunner.PollAsync(
            vars,
            OutcomeKey,
            AttemptsKey,
            timeoutMs: 5_000,
            pollIntervalMs: 10,
            Attempt);

        // Assert — both keys present and of the expected runtime types.
        Assert.True(vars.ContainsKey(OutcomeKey));
        Assert.True(vars.ContainsKey(AttemptsKey));
        Assert.IsType<StepOutcome>(vars[OutcomeKey]);
        Assert.IsType<List<AttemptRecord>>(vars[AttemptsKey]);
    }

    // -------------------------------------------------------------------------
    // Caller cancellation is honoured — the runner rethrows and forges no outcome
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PollAsync_CallerCancellationRethrowsAndWritesNoOutcome()
    {
        // Arrange — the caller's token is already cancelled.  The lambda would keep
        // returning Fail forever, so the only way out is the caller's cancellation.
        var vars = new Dictionary<string, object?>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Task<StepOutcome> Attempt(CancellationToken _)
            => Task.FromResult(new StepOutcome(Verdict.Fail, 1L, null));

        // Act + Assert — the caller's cancellation propagates (it must not be swallowed
        // and reclassified as a poll timeout).
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryRunner.PollAsync(
                vars,
                OutcomeKey,
                AttemptsKey,
                timeoutMs: 5_000,
                pollIntervalMs: 10,
                Attempt,
                ct: cts.Token));

        // Assert — the runner forged no normal result on the cancellation path: the
        // outcome key was never written.
        Assert.False(vars.ContainsKey(OutcomeKey));
    }
}
