using System;
using System.Globalization;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Bounds the container log a drill will read to the run that is happening now.
/// </summary>
/// <remarks>
/// <para>
/// The drills in this assembly assert on records the SERVER wrote — a broker naming the peer
/// principal it authenticated, an access log naming the client DN it verified. Those lines are
/// identical run to run, so a container orphaned by an earlier run carries a complete set of them.
/// Latch that container and every assertion passes against evidence the run never produced.
/// </para>
/// <para>
/// The drills exclude containers that were already running when they started, which closes the
/// common case. This closes it a second way and independently: <c>docker logs --since</c> from a
/// timestamp taken before the topology existed means even a wrongly-latched container can only
/// contribute lines written inside the run's own window.
/// </para>
/// <para>
/// MEASURED on this host: <c>docker logs --since 2026-08-07T09:46:09.0000000Z</c> is accepted and
/// filters (a line written before the timestamp was dropped, one after it kept, exit 0); and the
/// daemon's clock agreed with the host's to within the resolution of the probe — a
/// <c>date -u</c> inside a container fell between two host readings taken around it. The margin
/// below is therefore not needed on THIS host; it is there for hosts where the daemon runs in a VM
/// whose clock has drifted, and five seconds is far smaller than the interval between two runs of
/// the same drill (a topology start alone is ~20s) while being larger than any skew worth
/// tolerating silently.
/// </para>
/// </remarks>
internal static class DockerLogWindow
{
    /// <summary>The skew allowance described in the class remarks.</summary>
    private static readonly TimeSpan s_margin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The instant from which a drill starting NOW should read container logs.
    /// </summary>
    public static DateTimeOffset Start() => DateTimeOffset.UtcNow - s_margin;

    /// <summary>
    /// Formats <paramref name="instant"/> as the RFC 3339 UTC literal <c>docker logs --since</c>
    /// takes.
    /// </summary>
    public static string Since(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
