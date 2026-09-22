// The one fixture for rows whose refused connect must rest on a port the row itself holds,
// shared by the test assemblies that have such rows — issues #461 (born) and #527 (lifted here).
//
// WHY IT LIVES HERE RATHER THAN IN A TEST ASSEMBLY
// ───────────────────────────────────────────────
// It was born `private sealed` inside
// Vouchfx.Steps.CacheAssert.Elasticsearch.Tests/CacheAssertElasticsearchEmitTests.cs, where #461
// replaced a hard-coded port with a held reservation. Four other files in three other assemblies
// still named a port. Seven of those namings were the #461 defect proper — the row's expectation
// was that a connect is refused, which rested on a belief about the host rather than on anything
// the test established. The remaining two were staged connection values that no code ever dials
// (template resolution throws first), converted for uniformity while the files were open, so that
// neither file mixes the two kinds of port literal. A private nested class cannot be seen from
// another assembly, so the choice was four more copies or a lift. #527 lifted it.
//
// Copies of this particular fixture drift in the direction that silently disarms it: the value is
// entirely in the socket staying OPEN for the row's lifetime, and a copy that closes it after
// reading the port back still compiles, still passes, and is back to asserting a belief. One
// definition is the only way that property stays reviewed in one place.
//
// The standing BCL-only constraint of this project is met: System.Net.Sockets is BCL, so this
// adds no ProjectReference and no package, and any test assembly can reference it.
using System.Net;
using System.Net.Sockets;

namespace Vouchfx.TestSupport;

/// <summary>
/// A loopback endpoint that cannot answer, for rows that need a connect to be refused.  A TCP
/// socket is bound to an OS-allocated port and <see cref="Socket.Listen(int)"/> is never called;
/// the socket is held until <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why nothing answers: binding without listening puts no listening endpoint on the port, so an
/// inbound SYN is answered with RST and the connect fails with
/// <see cref="SocketError.ConnectionRefused"/>, which each consuming provider maps to
/// <c>EnvironmentError</c> — the verdict those rows assert.  MEASURED on this host (Windows
/// 10.0.26200, 2026-09-18): a connect to a held, non-listening reservation returns
/// <c>ConnectionRefused</c>, and no netstat row exists for the port at all.  MEASURED separately
/// on Linux (Ubuntu 24.04.4 LTS, x86-64, 2026-09-22): the same connect returns
/// <c>ConnectionRefused</c> too, unaffected by the <c>SO_REUSEADDR</c> fix below — that fix
/// touches only the reuse flag and never calls <see cref="Socket.Listen(int)"/>, so this half of
/// the contract held on Linux even before the fix; only the HOLD half (next paragraph) needed one.
/// </para>
/// <para>
/// Why it stays dead — the point of holding the socket rather than releasing it: while the
/// reservation is open the OS will not give the port to anything else, so there is no window in
/// which a stranger can answer.  MEASURED on this host (same run): a second bind of a held port
/// fails with <c>AddressAlreadyInUse</c>, a second bind that first sets <c>SO_REUSEADDR</c> fails
/// with <c>AccessDenied</c>, and after <see cref="Dispose"/> the port binds again — so the
/// reservation releases cleanly.  The alternative shape — bind to port 0, read the port back,
/// close the socket, then use the number — is what a test needs when it must LISTEN on the port
/// itself, and it is exactly what must not be used here: from the moment it closes, the port is
/// dead only by assumption, and the port it returns is in the ephemeral range the allocator draws
/// from.  That assumption is the defect #461 was filed for.
/// </para>
/// <para>
/// The HOLD half above was, until now, Windows-measured only, and it does not carry over to
/// Linux unaided: MEASURED on Linux (Ubuntu 24.04.4 LTS, x86-64, 2026-09-22), a bare reservation
/// is NOT exclusive there on its own — an ordinary second bind of the same address:port
/// SUCCEEDS. The cause is <c>SystemNative_Bind</c> (dotnet/runtime,
/// <c>src/native/libs/System.Native/pal_networking.c</c>), which sets <c>SO_REUSEADDR</c> on the
/// socket immediately before every TCP <see cref="Socket.Bind(EndPoint)"/> call on Unix —
/// unconditionally, and on both sides of a race, so the reservation and any "intruder" both
/// carry it by construction. Linux permits two <c>SO_REUSEADDR</c> sockets to share an
/// address:port as long as neither is listening, which is exactly this type's shape: a plain
/// bind of an already-bound port is EADDRINUSE on Linux only when NEITHER socket carries
/// <c>SO_REUSEADDR</c>, and .NET's own <see cref="Socket.Bind(EndPoint)"/> forces it onto every
/// TCP socket, so that premise does not hold for .NET specifically. <see cref="Reserve"/>
/// therefore clears <c>SO_REUSEADDR</c> on the reservation immediately after its own bind, via
/// <see cref="Socket.SetRawSocketOption"/> — a raw level/name pass-through to
/// <c>setsockopt(2)</c> with no bound-socket guard. The managed
/// <see cref="Socket.ExclusiveAddressUse"/> property cannot do this job: it throws
/// <see cref="InvalidOperationException"/> once the socket is bound, and setting it BEFORE
/// <see cref="Socket.Bind(EndPoint)"/> does not survive either, because
/// <c>SystemNative_Bind</c> re-asserts <c>SO_REUSEADDR</c> unconditionally on every bind,
/// overwriting whatever was set beforehand — only a change applied AFTER the reservation's own
/// bind persists. That timing is also why it works: Linux's bind-conflict check reads the
/// EXISTING owner's reuse flag at the moment of the SECOND bind, so clearing the reservation's
/// flag after its own bind is what makes a later intruder's bind fail. MEASURED with the fix
/// applied (same host, same date): the intruder's bind now fails with
/// <c>AddressAlreadyInUse</c>, matching the Windows behaviour above, and the connect-refused half
/// (previous paragraph) is unaffected. This is gated to <see cref="OperatingSystem.IsLinux"/>:
/// the raw <c>SOL_SOCKET</c>/<c>SO_REUSEADDR</c> values (1/2) used are Linux x86-64/arm64
/// numbers, differ on other platforms, and are unnecessary on Windows, where the measurement
/// above already holds without them.
/// </para>
/// <para>
/// The host is the IPv4 literal 127.0.0.1, not "localhost": the reservation is bound to
/// <see cref="IPAddress.Loopback"/>, so naming IPv4 keeps the reservation and the connect on one
/// stack — a dual-stack "localhost" could try ::1 first, a port nothing reserved.
/// </para>
/// <para>
/// SCOPE — what this type is for, and what it deliberately is not.  It covers one shape: a row
/// whose refused connect must rest on a port THE ROW ITSELF HOLDS, because the alternative is a
/// port in the OS's ephemeral allocation range that a stranger can be handed at any moment.  That
/// is the #461 defect, and there the reservation is the only honest answer.
/// </para>
/// <para>
/// Four neighbouring shapes look similar and are NOT this, so do not convert them on sight.  A
/// port deliberately chosen from OUTSIDE the ephemeral allocation range rests on a different and
/// sound premise — the allocator cannot hand such a port to a stranger, so no reservation is
/// needed to keep it free (MEASURED on this host, 2026-09-18: the Windows TCP dynamic range is
/// 49152 + 16384 ports, so a single-digit port sits far outside it; the range is configurable and
/// differs per platform, which is why the premise is "outside the range", not a specific number).
/// An unresolvable HOST NAME is not a port premise at all — nothing is being assumed about any
/// port, and a reservation would say nothing about name resolution.  A connection value that is
/// merely STAGED so a code path proceeds, and that nothing ever dials, has no reachability
/// premise to establish.  A port a test BINDS AND LISTENS on is likewise fine: there the test
/// owns the listener, so the port's state is established rather than assumed — and holding a
/// reservation is precisely what such a test must NOT do.
/// </para>
/// </remarks>
public sealed class DeadLoopbackEndpoint : IDisposable
{
    private readonly Socket _reservation;

    private DeadLoopbackEndpoint(Socket reservation, IPEndPoint bound)
    {
        _reservation = reservation;
        Host = bound.Address.ToString();
        Port = bound.Port;
    }

    /// <summary>
    /// The address the reservation is bound to, read back off the socket rather than restated —
    /// so a row asserting the host is absent from an observation is checking the host this run
    /// really used.
    /// </summary>
    public string Host { get; }

    /// <summary>The OS-allocated port the reservation holds.</summary>
    public int Port { get; }

    /// <summary>
    /// <see cref="Host"/> and <see cref="Port"/> as <c>host:port</c> — the form every consuming
    /// connection string embeds.  Deliberately not a URL: the consumers span HTTP, AMQP and the
    /// Redis comma-separated grammar, so each formats its own scheme around this.
    /// </summary>
    public string Authority => $"{Host}:{Port}";

    /// <summary>Binds — but does not listen on — an OS-allocated loopback port.</summary>
    /// <returns>A reservation that holds the port until it is disposed.</returns>
    public static DeadLoopbackEndpoint Reserve()
    {
        var reservation = new Socket(
            AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Port 0: the OS picks a port it considers unused. The successful bind is the
            // evidence it was free; holding it is what keeps it that way.
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            if (OperatingSystem.IsLinux())
            {
                // Undo the SO_REUSEADDR that SystemNative_Bind (dotnet/runtime,
                // pal_networking.c) forces onto every TCP socket immediately before the bind()
                // call above — without this, Linux lets a later, equally SO_REUSEADDR-carrying
                // bind take the port out from under the reservation (see the type-level
                // remarks). Must run AFTER Bind: the managed ExclusiveAddressUse setter refuses
                // to run once bound, and running it before Bind is undone by Bind itself
                // re-asserting SO_REUSEADDR. The raw level/name pass-through carries neither
                // restriction.
                const int SolSocket = 1;   // Linux x86-64/arm64 SOL_SOCKET
                const int SoReuseAddr = 2; // Linux x86-64/arm64 SO_REUSEADDR
                reservation.SetRawSocketOption(SolSocket, SoReuseAddr, BitConverter.GetBytes(0));
            }

            return new DeadLoopbackEndpoint(
                reservation, (IPEndPoint)reservation.LocalEndPoint!);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    /// <summary>Releases the port. Nothing else may use it before this runs.</summary>
    public void Dispose() => _reservation.Dispose();
}
