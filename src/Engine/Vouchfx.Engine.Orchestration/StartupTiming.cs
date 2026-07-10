namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Captures the wall-clock durations of the three phases of
/// <see cref="StubTopology.StartAsync"/>: the full build-and-start sequence, the database
/// health gate, and the service health gate.
/// </summary>
/// <param name="Total">
/// Wall-clock time from the start of <see cref="StubTopology.StartAsync"/> until both health
/// gates have completed successfully.  Covers the Aspire build, DCP start, container pull,
/// database initialisation, and the <c>"web"</c> service health check.
/// </param>
/// <param name="DatabaseReady">
/// Wall-clock time from the start of <see cref="StubTopology.StartAsync"/> until the
/// <c>"appdb"</c> health gate completes.  This is the cumulative time spent on the Aspire
/// build, DCP start, container pull, and database lifecycle initialisation.
/// </param>
/// <param name="ServiceReady">
/// Additional wall-clock time elapsed between the <c>"appdb"</c> gate completing and the
/// <c>"web"</c> gate completing.  A small value here indicates the service started in
/// parallel with Postgres; a large value may indicate the service image pull was slow or
/// the HTTP health-check probe required several retries.
/// </param>
public sealed record StartupTiming(TimeSpan Total, TimeSpan DatabaseReady, TimeSpan ServiceReady);
