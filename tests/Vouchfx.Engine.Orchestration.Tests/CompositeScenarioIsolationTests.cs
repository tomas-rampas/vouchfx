// Tests for CompositeScenarioIsolation (state-reset generalisation): fans
// BeginScenarioAsync/EndScenarioAsync out to N child IScenarioIsolation instances so
// EVERY resettable dependency a topology declares is reset between scenarios.
//
// Uses recording fakes (no Docker, no real Respawn/database) to pin:
//   • children invoked in declaration order;
//   • End fail-fast — a child's OrchestrationException stops later children;
//   • DisposeAsync disposes ALL children even when one throws;
//   • Begin/End after dispose throw ObjectDisposedException.
//
// Run with: dotnet test --filter "requires!=docker&FullyQualifiedName~CompositeScenarioIsolation"

using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="CompositeScenarioIsolation"/> using recording fakes.
/// </summary>
public sealed class CompositeScenarioIsolationTests
{
    /// <summary>
    /// A fake <see cref="IScenarioIsolation"/> that records every Begin/End/Dispose
    /// call it receives, and can be configured to throw on any of the three.
    /// </summary>
    private sealed class RecordingIsolation : IScenarioIsolation, IAsyncDisposable
    {
        private readonly string _name;
        private readonly List<string> _log;
        private readonly bool _throwOnBegin;
        private readonly bool _throwOnEnd;
        private readonly bool _throwOnDispose;

        public RecordingIsolation(
            string name,
            List<string> log,
            bool throwOnBegin = false,
            bool throwOnEnd = false,
            bool throwOnDispose = false)
        {
            _name = name;
            _log = log;
            _throwOnBegin = throwOnBegin;
            _throwOnEnd = throwOnEnd;
            _throwOnDispose = throwOnDispose;
        }

        public bool Disposed { get; private set; }

        public Task BeginScenarioAsync(CancellationToken ct)
        {
            _log.Add($"{_name}:Begin");
            if (_throwOnBegin)
            {
                throw new OrchestrationException(
                    new OrchestrationErrorInfo(
                        OrchestrationErrorKind.Provision, _name, null, null, "begin failed"));
            }

            return Task.CompletedTask;
        }

        public Task EndScenarioAsync(CancellationToken ct)
        {
            _log.Add($"{_name}:End");
            if (_throwOnEnd)
            {
                throw new OrchestrationException(
                    new OrchestrationErrorInfo(
                        OrchestrationErrorKind.Provision, _name, null, null, "end failed"));
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _log.Add($"{_name}:Dispose");
            Disposed = true;
            if (_throwOnDispose)
            {
                throw new InvalidOperationException($"{_name} dispose failed");
            }

            return ValueTask.CompletedTask;
        }
    }

    // ── Declaration-order invocation ──────────────────────────────────────────

    /// <summary>
    /// <see cref="CompositeScenarioIsolation.BeginScenarioAsync"/> invokes every
    /// child in the order supplied to the constructor.
    /// </summary>
    [Fact]
    public async Task BeginScenarioAsync_InvokesChildrenInDeclarationOrder()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log);
        var b = new RecordingIsolation("b", log);
        var c = new RecordingIsolation("c", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a, b, c });

        await sut.BeginScenarioAsync(CancellationToken.None);

        Assert.Equal(new List<string> { "a:Begin", "b:Begin", "c:Begin" }, log);
    }

    /// <summary>
    /// <see cref="CompositeScenarioIsolation.EndScenarioAsync"/> invokes every child
    /// in the order supplied to the constructor.
    /// </summary>
    [Fact]
    public async Task EndScenarioAsync_InvokesChildrenInDeclarationOrder()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log);
        var b = new RecordingIsolation("b", log);
        var c = new RecordingIsolation("c", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a, b, c });

        await sut.EndScenarioAsync(CancellationToken.None);

        Assert.Equal(new List<string> { "a:End", "b:End", "c:End" }, log);
    }

    // ── Fail-fast ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When the first child's <c>EndScenarioAsync</c> throws
    /// <see cref="OrchestrationException"/>, the exception propagates and the
    /// remaining children are NOT invoked (fail-fast).
    /// </summary>
    [Fact]
    public async Task EndScenarioAsync_FirstChildThrows_LaterChildrenNotInvoked()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log, throwOnEnd: true);
        var b = new RecordingIsolation("b", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a, b });

        var ex = await Assert.ThrowsAsync<OrchestrationException>(
            () => sut.EndScenarioAsync(CancellationToken.None));

        // The escaping exception's ResourceName names the failing dependency.
        Assert.Equal("a", ex.Info.ResourceName);

        // 'a' was invoked (and threw); 'b' must NOT have been invoked.
        Assert.Equal(new List<string> { "a:End" }, log);
    }

    /// <summary>
    /// When a MIDDLE child's <c>EndScenarioAsync</c> throws, the earlier children have
    /// already run and the later children are NOT invoked.
    /// </summary>
    [Fact]
    public async Task EndScenarioAsync_MiddleChildThrows_EarlierRanLaterDidNot()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log);
        var b = new RecordingIsolation("b", log, throwOnEnd: true);
        var c = new RecordingIsolation("c", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a, b, c });

        var ex = await Assert.ThrowsAsync<OrchestrationException>(
            () => sut.EndScenarioAsync(CancellationToken.None));

        Assert.Equal("b", ex.Info.ResourceName);
        Assert.Equal(new List<string> { "a:End", "b:End" }, log);
    }

    /// <summary>
    /// The same fail-fast contract holds for <c>BeginScenarioAsync</c>.
    /// </summary>
    [Fact]
    public async Task BeginScenarioAsync_FirstChildThrows_LaterChildrenNotInvoked()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log, throwOnBegin: true);
        var b = new RecordingIsolation("b", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a, b });

        await Assert.ThrowsAsync<OrchestrationException>(
            () => sut.BeginScenarioAsync(CancellationToken.None));

        Assert.Equal(new List<string> { "a:Begin" }, log);
    }

    // ── DisposeAsync — best-effort across all children ────────────────────────

    /// <summary>
    /// <see cref="CompositeScenarioIsolation.DisposeAsync"/> disposes every child
    /// even when an earlier child's disposal throws.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DisposesAllChildren_EvenWhenOneThrows()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log, throwOnDispose: true);
        var b = new RecordingIsolation("b", log);
        var c = new RecordingIsolation("c", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a, b, c });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.DisposeAsync().AsTask());

        Assert.Contains("a dispose failed", ex.Message, StringComparison.Ordinal);
        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
        Assert.True(c.Disposed);
    }

    /// <summary>
    /// <see cref="CompositeScenarioIsolation.DisposeAsync"/> is idempotent — a second
    /// call is a no-op and does not re-invoke the children's disposal.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_SecondCall_IsNoOp()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a });

        await sut.DisposeAsync();
        var countAfterFirst = log.Count;

        await sut.DisposeAsync();

        Assert.Equal(countAfterFirst, log.Count);
    }

    // ── Post-dispose guards ────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="CompositeScenarioIsolation.BeginScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task BeginScenarioAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a });
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.BeginScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="CompositeScenarioIsolation.EndScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task EndScenarioAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var log = new List<string>();
        var a = new RecordingIsolation("a", log);
        var sut = new CompositeScenarioIsolation(new List<IScenarioIsolation> { a });
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.EndScenarioAsync(CancellationToken.None));
    }

    // ── Constructor validation ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="CompositeScenarioIsolation"/> rejects a <see langword="null"/>
    /// children list at construction time.
    /// </summary>
    [Fact]
    public void Constructor_NullChildren_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeScenarioIsolation(null!));
    }
}
