// Vouchfx.Engine.Abstractions — RetryRunner (§7 verifyMode RETRY, §12.1, §14).
//
// The engine-owned polling loop behind `verifyMode: RETRY`.  Authors never write
// Thread.Sleep; the engine owns the backoff timeline.  Each invocation of the step
// delegate is recorded as an AttemptRecord so the RETRY timeline is renderable from
// the event stream without re-running the suite (§14).
//
// Memory-model note (§5): this is a STATELESS static class — no mutable static
// fields.  A fresh Polly v8 pipeline is constructed per PollAsync call so nothing
// roots the collectible AssemblyLoadContext.  The values written into the host's
// Vars dictionary (the StepOutcome and the List<AttemptRecord>) are pure data
// records.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Engine.Abstractions.Retry;

/// <summary>
/// The engine-owned RETRY runner: repeatedly invokes a step delegate with bounded
/// exponential backoff until it passes, terminates, or the overall window elapses,
/// then writes the final <see cref="StepOutcome"/> and the per-attempt timeline into
/// the shared <c>Vars</c> dictionary (§7, §12.1, §14).
/// </summary>
/// <remarks>
/// <para>
/// This class is <strong>stateless</strong> by design (§5 memory model): it holds no
/// mutable static state and builds a fresh Polly v8
/// <see cref="ResiliencePipeline{TResult}"/> on every call.  Nothing here can root a
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// </para>
/// <para>
/// <strong>Verdict semantics (§12.1).</strong> A RETRY that is never satisfied
/// resolves to <see cref="Verdict.Inconclusive"/> — <em>never</em>
/// <see cref="Verdict.Fail"/>.  A <see cref="Verdict.Pass"/> stops the loop with a
/// pass; a <see cref="Verdict.EnvironmentError"/> (whether returned or synthesised
/// from a thrown attempt) is terminal — the runner does not keep hammering a dead
/// dependency.  <see cref="Verdict.Fail"/> and <see cref="Verdict.Inconclusive"/>
/// results are retried with backoff until the window elapses.
/// </para>
/// </remarks>
public static class RetryRunner
{
    /// <summary>
    /// The default overall polling window when the step declares no <c>timeout</c>.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The default base delay between attempts when the step declares no poll
    /// interval.  Exponential backoff multiplies this by <see cref="BackoffFactor"/>
    /// per attempt, capped at <see cref="MaxDelayCap"/>.
    /// </summary>
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The hard ceiling applied to the computed backoff delay so a long window never
    /// stalls behind a single very large wait.
    /// </summary>
    public static readonly TimeSpan MaxDelayCap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The exponential growth factor applied to the base delay between attempts.
    /// </summary>
    public static readonly double BackoffFactor = 2.0;

    /// <summary>
    /// A defensive upper bound on the number of attempts, independent of the time
    /// window, so a misconfigured zero-delay loop cannot spin unboundedly.
    /// </summary>
    public static readonly int MaxAttemptsHardCap = 1000;

    /// <summary>
    /// Polls <paramref name="attempt"/> with bounded exponential backoff until it
    /// passes, terminates, or the overall window elapses, then writes the final
    /// <see cref="StepOutcome"/> and the <see cref="List{T}"/> of
    /// <see cref="AttemptRecord"/> into <paramref name="vars"/>.
    /// </summary>
    /// <param name="vars">
    /// The shared <c>ScriptGlobalVariables.Vars</c> dictionary.  On return,
    /// <paramref name="outcomeKey"/> holds the final <see cref="StepOutcome"/> and
    /// <paramref name="attemptsKey"/> holds a <see cref="List{T}"/> of
    /// <see cref="AttemptRecord"/>.
    /// </param>
    /// <param name="outcomeKey">
    /// The <c>Vars</c> key under which the final <see cref="StepOutcome"/> is written
    /// (typically <c>VarKeys.Outcome(sanitisedStepId)</c>).
    /// </param>
    /// <param name="attemptsKey">
    /// The <c>Vars</c> key under which the per-attempt timeline is written
    /// (typically <c>VarKeys.Attempts(sanitisedStepId)</c>).
    /// </param>
    /// <param name="timeoutMs">
    /// The overall polling window in milliseconds, or <see langword="null"/> to use
    /// <see cref="DefaultTimeout"/>.  A <see langword="null"/> or non-positive value
    /// uses the engine default.
    /// </param>
    /// <param name="pollIntervalMs">
    /// The base delay between attempts in milliseconds, or <see langword="null"/> to
    /// use <see cref="DefaultBaseDelay"/>.  A <see langword="null"/> or non-positive
    /// value uses the engine default.
    /// </param>
    /// <param name="attempt">
    /// The step delegate to poll.  Each invocation must return a
    /// <see cref="StepOutcome"/>; a thrown (non-cancellation) exception is treated as
    /// a terminal <see cref="Verdict.EnvironmentError"/>.
    /// </param>
    /// <param name="ct">
    /// A caller cancellation token, linked with the internal window timer.
    /// </param>
    /// <returns>A task that completes once <paramref name="vars"/> has been written.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="vars"/>, <paramref name="outcomeKey"/>,
    /// <paramref name="attemptsKey"/>, or <paramref name="attempt"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Back-compat overload (issue #262): delegates to the sink-aware overload with
    /// <c>sink: null</c>, so every existing call site keeps compiling and behaving
    /// identically — no live <c>step-attempt</c> signal is emitted on this path.
    /// </remarks>
    public static Task PollAsync(
        IDictionary<string, object?> vars,
        string outcomeKey,
        string attemptsKey,
        long? timeoutMs,
        long? pollIntervalMs,
        Func<CancellationToken, Task<StepOutcome>> attempt,
        CancellationToken ct = default)
        => PollAsync(vars, outcomeKey, attemptsKey, timeoutMs, pollIntervalMs, attempt, sink: null, stepId: string.Empty, ct);

    /// <summary>
    /// Polls <paramref name="attempt"/> with bounded exponential backoff until it
    /// passes, terminates, or the overall window elapses, then writes the final
    /// <see cref="StepOutcome"/> and the <see cref="List{T}"/> of
    /// <see cref="AttemptRecord"/> into <paramref name="vars"/> — additionally
    /// reporting each recorded attempt to <paramref name="sink"/> in real time as it
    /// resolves (issue #262), so a live <c>--events-stream</c> tail can observe a
    /// RETRY step's polling timeline WHILE it is still in progress, not only after
    /// this method returns.
    /// </summary>
    /// <param name="vars">
    /// The shared <c>ScriptGlobalVariables.Vars</c> dictionary.  On return,
    /// <paramref name="outcomeKey"/> holds the final <see cref="StepOutcome"/> and
    /// <paramref name="attemptsKey"/> holds a <see cref="List{T}"/> of
    /// <see cref="AttemptRecord"/>.
    /// </param>
    /// <param name="outcomeKey">
    /// The <c>Vars</c> key under which the final <see cref="StepOutcome"/> is written
    /// (typically <c>VarKeys.Outcome(sanitisedStepId)</c>).
    /// </param>
    /// <param name="attemptsKey">
    /// The <c>Vars</c> key under which the per-attempt timeline is written
    /// (typically <c>VarKeys.Attempts(sanitisedStepId)</c>).
    /// </param>
    /// <param name="timeoutMs">
    /// The overall polling window in milliseconds, or <see langword="null"/> to use
    /// <see cref="DefaultTimeout"/>.  A <see langword="null"/> or non-positive value
    /// uses the engine default.
    /// </param>
    /// <param name="pollIntervalMs">
    /// The base delay between attempts in milliseconds, or <see langword="null"/> to
    /// use <see cref="DefaultBaseDelay"/>.  A <see langword="null"/> or non-positive
    /// value uses the engine default.
    /// </param>
    /// <param name="attempt">
    /// The step delegate to poll.  Each invocation must return a
    /// <see cref="StepOutcome"/>; a thrown (non-cancellation) exception is treated as
    /// a terminal <see cref="Verdict.EnvironmentError"/>.
    /// </param>
    /// <param name="sink">
    /// The host-side per-step live event sink (§14, issue #262), or
    /// <see langword="null"/> when no live conduit is configured — in which case this
    /// overload behaves identically to the classic 6-argument overload (a pure no-op
    /// signal path).
    /// </param>
    /// <param name="stepId">
    /// The raw (un-sanitised) step identifier reported to <paramref name="sink"/>.
    /// Ignored (never read) when <paramref name="sink"/> is <see langword="null"/>.
    /// </param>
    /// <param name="ct">
    /// A caller cancellation token, linked with the internal window timer.
    /// </param>
    /// <returns>A task that completes once <paramref name="vars"/> has been written.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="vars"/>, <paramref name="outcomeKey"/>,
    /// <paramref name="attemptsKey"/>, or <paramref name="attempt"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static async Task PollAsync(
        IDictionary<string, object?> vars,
        string outcomeKey,
        string attemptsKey,
        long? timeoutMs,
        long? pollIntervalMs,
        Func<CancellationToken, Task<StepOutcome>> attempt,
        IStepEventSink? sink,
        string stepId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentException.ThrowIfNullOrEmpty(outcomeKey);
        ArgumentException.ThrowIfNullOrEmpty(attemptsKey);
        ArgumentNullException.ThrowIfNull(attempt);

        var window = timeoutMs is { } t and > 0 ? TimeSpan.FromMilliseconds(t) : DefaultTimeout;
        var baseDelay = pollIntervalMs is { } p and > 0 ? TimeSpan.FromMilliseconds(p) : DefaultBaseDelay;

        var attempts = new List<AttemptRecord>();
        var overall = Stopwatch.StartNew();

        // Link the caller's token with the overall-window timer.  A cancellation from the
        // caller's own token is rethrown (the async cancellation contract — see the filtered
        // catch below); only the internal window timer elapsing maps to Inconclusive (timeout).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(window);

        var pipeline = BuildPipeline(baseDelay);

        StepOutcome finalOutcome;
        try
        {
            var result = await pipeline
                .ExecuteAsync(
                    async token =>
                    {
                        var perAttempt = Stopwatch.StartNew();
                        var index = attempts.Count + 1;
                        StepOutcome outcome;
                        try
                        {
                            outcome = await attempt(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancellation (caller token or window timer) — surface to the
                            // outer handlers: caller-cancellation is rethrown verbatim, while
                            // a window-timer elapse is classified as Inconclusive (timeout).
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // A thrown attempt is a terminal EnvironmentError: record
                            // it and return it so ShouldHandle stops the loop.  Emit only
                            // the exception TYPE name — a raw ex.Message can leak connection
                            // strings, credentials or hostnames into the author-visible
                            // observation / event stream / terminal output.  Full exception
                            // detail belongs in structured logging, not here (§17 spirit).
                            var observation = JsonSerializer.Serialize(
                                new Dictionary<string, string> { ["error"] = ex.GetType().Name });
                            outcome = new StepOutcome(
                                Verdict.EnvironmentError, perAttempt.ElapsedMilliseconds, observation);
                            var thrownRecord = new AttemptRecord(
                                index, perAttempt.ElapsedMilliseconds, outcome.Verdict, outcome.Observation);
                            attempts.Add(thrownRecord);
                            // Issue #262: report the attempt to the live sink the moment it
                            // resolves — the only place with genuine per-attempt real-time
                            // timing. A null sink (no --events-stream conduit) is a no-op.
                            sink?.OnStepAttempt(stepId, thrownRecord);
                            return outcome;
                        }

                        var record = new AttemptRecord(
                            index, perAttempt.ElapsedMilliseconds, outcome.Verdict, outcome.Observation);
                        attempts.Add(record);
                        sink?.OnStepAttempt(stepId, record);
                        return outcome;
                    },
                    linked.Token)
                .ConfigureAwait(false);

            finalOutcome = Classify(result, attempts.Count, overall.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER cancelled (their own token).  Honour the async cancellation
            // contract: rethrow without writing a forged outcome — this is not a poll
            // timeout, so it must not be classified as Inconclusive (§12.1).
            throw;
        }
        catch (OperationCanceledException)
        {
            // The internal window timer elapsed before a terminal result was reached —
            // Inconclusive (timeout), never Fail (§12.1).
            finalOutcome = TimedOut(attempts.Count, overall.ElapsedMilliseconds);
        }

        vars[attemptsKey] = attempts;
        vars[outcomeKey] = finalOutcome;
    }

    // Builds a fresh generic retry pipeline.  ShouldHandle retries only on the
    // retryable verdicts (Fail, Inconclusive); Pass and EnvironmentError stop the
    // loop.  MaxRetryAttempts is bounded by MaxAttemptsHardCap (attempts = retries+1).
    private static ResiliencePipeline<StepOutcome> BuildPipeline(TimeSpan baseDelay)
    {
        return new ResiliencePipelineBuilder<StepOutcome>()
            .AddRetry(new RetryStrategyOptions<StepOutcome>
            {
                ShouldHandle = static args => new ValueTask<bool>(
                    args.Outcome.Result is { Verdict: Verdict.Fail or Verdict.Inconclusive }),
                MaxRetryAttempts = MaxAttemptsHardCap - 1,
                Delay = baseDelay,
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = MaxDelayCap,
                UseJitter = true,
            })
            .Build();
    }

    // Maps the pipeline's final result to the runner's final outcome.  Pass and
    // EnvironmentError pass through verbatim with the overall duration; an exhausted
    // retry (the pipeline gave up while still Fail/Inconclusive) becomes Inconclusive.
    private static StepOutcome Classify(StepOutcome result, int attemptCount, long elapsedMs)
        => result.Verdict switch
        {
            Verdict.Pass => new StepOutcome(Verdict.Pass, elapsedMs, result.Observation),
            Verdict.EnvironmentError => new StepOutcome(
                Verdict.EnvironmentError, elapsedMs, result.Observation),
            _ => Exhausted(attemptCount, elapsedMs),
        };

    private static StepOutcome TimedOut(int attemptCount, long elapsedMs)
        => new(
            Verdict.Inconclusive,
            elapsedMs,
            JsonSerializer.Serialize(new RetryGiveUp("retry-timeout", attemptCount)));

    private static StepOutcome Exhausted(int attemptCount, long elapsedMs)
        => new(
            Verdict.Inconclusive,
            elapsedMs,
            JsonSerializer.Serialize(new RetryGiveUp("retry-exhausted", attemptCount)));

    // Pure DTO for the give-up observation JSON: {"reason":"retry-timeout","attempts":N}.
    // The explicit JSON property names lock the lower-case keys mandated by the
    // observation contract, independent of any ambient serialiser options.
    private sealed record RetryGiveUp(
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("attempts")] int Attempts);
}
