// Vouchfx.TestSupport — TestCertificateStoreGuard (#419).
//
// The non-regression half of the certificate-store hygiene work. TestCertificateStoreSweep
// (in TestCertificateAuthority.cs) removes a bed's cached copies when that bed is disposed;
// this class answers the question the sweep cannot: did the sweep actually happen, for
// everything?
//
// Deliberately references NO xUnit type. The scan and the census are the reusable part and
// belong to every test assembly that mints certificates; the mechanism that turns a census into
// a red run is xUnit-version- and assembly-specific, so it lives in the consuming test project
// (see Vouchfx.Engine.Runtime.Tests/CertificateStoreHygieneGuardTests.cs). That split also keeps
// this project's stated property — it takes an Assembly and references no Vouchfx type — intact.
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Vouchfx.TestSupport;

/// <summary>
/// Detects certificates minted by THIS process that are still cached in Windows'
/// intermediate-CA stores, removes what it can, and describes what it found.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a guard as well as a sweep.</strong> Every bed sweeps its own material on
/// <c>Dispose</c>, which closes the leak on the path where <c>Dispose</c> runs. A process that
/// crashes, is cancelled, or is killed by a runner timeout skips that path entirely and leaves
/// residue behind, and nothing noticed: the accumulation that eventually broke chain building was
/// found by hand, twice, after roughly an hour of diagnosis each time (#374). A run that ends
/// dirtier than it started should say so at the moment it happens, in the run that caused it.
/// </para>
/// <para>
/// <strong>Why the key is the process token and nothing broader.</strong> Detection matches
/// <see cref="TestCertificateAuthority.ProcessTokenMarker"/> inside the SUBJECT — the token with
/// the separator minting always puts before it, so that mint and search share one spelling —
/// which is possible only because authority subjects are now per-process unique. A broader
/// subject match — the bare
/// common names, or a <c>Vouchfx</c> prefix — would sweep in the cached intermediates of a
/// CONCURRENTLY RUNNING suite; removing one of those mid-run can fail that run's chain build,
/// trading a cosmetic leak for an intermittent failure in a different process. That is the same
/// reasoning that makes the per-bed sweep match on thumbprint, applied to the one key that is
/// both broader than a thumbprint and still provably this process's own.
/// </para>
/// <para>
/// <strong>Removal is best effort; detection is not.</strong> Writing to
/// <c>LocalMachine\CA</c> needs elevation a test process usually lacks, so a failed removal is
/// reported in the census rather than thrown — but the census itself is still returned, and the
/// caller still fails. A store this process cannot even READ is skipped silently: nothing can be
/// concluded about it, and a guard that reported an unreadable store as residue would be crying
/// wolf. No-op off Windows, where nothing performs this caching.
/// </para>
/// </remarks>
public static class TestCertificateStoreGuard
{
    /// <summary>
    /// Scans <c>CurrentUser\CA</c> and <c>LocalMachine\CA</c> for certificates carrying this
    /// process's token, attempts to remove any it finds, and returns a census of them.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the stores hold nothing minted by this process — the expected
    /// result. Otherwise a multi-line census naming the store, subject and thumbprint of every
    /// stranded certificate, and whether removing it succeeded, suitable as an assertion message.
    /// </returns>
    public static string? SweepProcessResidue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var census = new List<string>();
        SweepLocation(StoreLocation.CurrentUser, census);
        SweepLocation(StoreLocation.LocalMachine, census);

        if (census.Count == 0)
        {
            return null;
        }

        return $"Certificate-store residue (#374/#419): {census.Count} certificate(s) minted by " +
            $"this process (token {TestCertificateAuthority.ProcessToken}) were still in the " +
            "intermediate-CA stores when the suite finished. Every bed removes its own cached " +
            "copies on Dispose, so anything listed below escaped that path — a bed whose Dispose " +
            "did not run, or a copy cached under a thumbprint no bed listed. Removal was " +
            "attempted and each line records whether it succeeded; a line reading 'removal " +
            "failed' is still on the host and needs clearing by hand. Note that this check only " +
            "runs on Windows, and every job in .github/workflows/build.yml runs on ubuntu-latest, " +
            "so the run it fails is a local one." + Environment.NewLine +
            string.Join(Environment.NewLine, census);
    }

    private static void SweepLocation(StoreLocation location, List<string> census)
    {
        X509Certificate2Collection stranded;

        try
        {
            // Read-only probe first, exactly as TestCertificateStoreSweep does: opening a machine
            // store for WRITING fails without elevation, and the overwhelmingly common case is
            // that there is nothing to remove.
            using var probe = new X509Store(StoreName.CertificateAuthority, location);
            probe.Open(OpenFlags.ReadOnly);
            stranded = OwnTokenCertificates(probe);
        }
        catch (Exception ex) when (
            ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            // Unreadable store: no evidence either way, so no claim either way.
            return;
        }

        if (stranded.Count == 0)
        {
            return;
        }

        // Every certificate here owns a native handle of its own, so they must be disposed whether
        // removal succeeds or throws — a guard that leaked handles while reporting a leak would
        // be a poor joke.
        try
        {
            var outcomes = TryRemove(location, stranded);

            for (var i = 0; i < stranded.Count; i++)
            {
                var certificate = stranded[i];
                census.Add(
                    $"  {location}\\CA  {certificate.Subject}  thumbprint {certificate.Thumbprint}  ({outcomes[i]})");
            }
        }
        finally
        {
            foreach (var certificate in stranded)
            {
                certificate.Dispose();
            }
        }
    }

    /// <returns>
    /// One outcome per entry of <paramref name="stranded"/>, positionally. Per certificate rather
    /// than one verdict for the batch: a store that rejects the third of five removals says
    /// nothing about the other four, and reporting all five as failed would send someone hunting
    /// for four certificates that are no longer there.
    /// </returns>
    private static string[] TryRemove(StoreLocation location, X509Certificate2Collection stranded)
    {
        var outcomes = new string[stranded.Count];

        using var store = new X509Store(StoreName.CertificateAuthority, location);
        try
        {
            store.Open(OpenFlags.ReadWrite);
        }
        catch (Exception ex) when (
            ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            // The store itself could not be opened for writing — the unelevated machine-store
            // case. That verdict genuinely IS shared by every certificate in it.
            Array.Fill(outcomes, $"removal failed: {ex.GetType().Name}: {ex.Message}");
            return outcomes;
        }

        for (var i = 0; i < stranded.Count; i++)
        {
            try
            {
                store.Remove(stranded[i]);
                outcomes[i] = "removed";
            }
            catch (Exception ex) when (
                ex is CryptographicException or UnauthorizedAccessException or SecurityException)
            {
                outcomes[i] = $"removal failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        return outcomes;
    }

    /// <remarks>
    /// <para>
    /// Enumerates rather than <c>Find</c>s because the key is a SUBSTRING of the subject and
    /// <see cref="X509FindType.FindBySubjectName"/> has its own matching rules. The non-matching
    /// instances are disposed here: <see cref="X509Store.Certificates"/> hands back fresh
    /// certificate objects, each holding a native handle, and a whole store's worth of them is not
    /// something to leave to the finalizer. The matched ones are disposed on the throw path too,
    /// since a caller that never receives the collection cannot dispose it.
    /// </para>
    /// <para>
    /// The needle is <see cref="TestCertificateAuthority.ProcessTokenMarker"/> — the token WITH
    /// the separator every minted subject puts before it — and not the bare token. Eight hex
    /// characters unanchored appear inside real subjects on an ordinary Windows host (TPM
    /// attestation intermediates embed long hexadecimal key identifiers), and a false match here
    /// deletes somebody's device certificate. <see cref="CarriesProcessToken"/> additionally
    /// requires the token not to run on into more hex digits.
    /// </para>
    /// </remarks>
    private static X509Certificate2Collection OwnTokenCertificates(X509Store store)
    {
        var matches = new X509Certificate2Collection();

        // Hoisted: X509Store.Certificates materialises a NEW collection of live handles on every
        // access, so the throw path below needs the same instance the loop is walking.
        var all = store.Certificates;

        try
        {
            foreach (var certificate in all)
            {
                if (CarriesProcessToken(certificate.Subject))
                {
                    matches.Add(certificate);
                }
                else
                {
                    certificate.Dispose();
                }
            }
        }
        catch
        {
            // Reading Subject can throw (CryptographicException on a malformed DN), and the
            // handles this method never reached are as much its responsibility as the ones it
            // matched. Dispose EVERYTHING; double-dispose is a no-op.
            foreach (var certificate in all)
            {
                certificate.Dispose();
            }

            throw;
        }

        return matches;
    }

    /// <remarks>
    /// The marker must be followed by end-of-string or a NON-hex character, so this process's
    /// <c>" a1b2c3d4"</c> cannot match inside another subject's longer run <c>" a1b2c3d4ef"</c>.
    /// Costs one character comparison and closes the only false-positive shape the space anchor
    /// leaves open.
    /// </remarks>
    private static bool CarriesProcessToken(string subject)
    {
        var marker = TestCertificateAuthority.ProcessTokenMarker;

        for (var at = subject.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            at >= 0;
            at = subject.IndexOf(marker, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = at + marker.Length;
            if (after >= subject.Length || !Uri.IsHexDigit(subject[after]))
            {
                return true;
            }
        }

        return false;
    }
}
