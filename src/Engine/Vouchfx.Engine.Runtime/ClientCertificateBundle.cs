using System.Security.Cryptography;

namespace Vouchfx.Engine.Runtime;

/// <summary>Parses public PEM certificate blocks without a chain build or certificate-store access.</summary>
internal static class ClientCertificateBundle
{
    internal const int MaximumCertificates = 32;
    internal const int MaximumPemCharacters = 4 * 1024 * 1024;

    internal static IReadOnlyList<byte[]> Parse(string pem, ReadOnlySpan<byte> expectedLeaf)
    {
        var certificates = Parse(pem);
        if (!certificates[0].AsSpan().SequenceEqual(expectedLeaf))
        {
            throw new CryptographicException("The client certificate PEM must contain the key-matching leaf first.");
        }
        return certificates;
    }

    internal static IReadOnlyList<byte[]> Parse(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        if (pem.Length > MaximumPemCharacters)
        {
            throw new CryptographicException("The client certificate PEM exceeds the 4 MiB limit.");
        }

        var certificates = new List<byte[]>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var remaining = pem.AsSpan();
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            if (remaining[fields.Label].SequenceEqual("CERTIFICATE"))
            {
                if (certificates.Count == MaximumCertificates)
                {
                    throw new CryptographicException("The client certificate PEM exceeds the 32-certificate limit.");
                }
                var der = Convert.FromBase64String(remaining[fields.Base64Data].ToString());
                if (!identities.Add(Convert.ToHexString(SHA256.HashData(der))))
                {
                    throw new CryptographicException("The client certificate PEM contains a duplicate certificate.");
                }
                certificates.Add(der);
            }
            remaining = remaining[fields.Location.End..];
        }

        // TryFind skips malformed blocks. A visible certificate marker without a parsed
        // certificate must be refused rather than silently losing a supplied chain link.
        var declaredBlocks = pem.Split("-----BEGIN CERTIFICATE-----", StringSplitOptions.None).Length - 1;
        if (certificates.Count == 0 || certificates.Count != declaredBlocks)
        {
            throw new CryptographicException("The client certificate PEM contains a missing or malformed certificate block.");
        }
        return certificates.AsReadOnly();
    }
}
