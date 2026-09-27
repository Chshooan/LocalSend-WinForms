using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalSendWinForms.Core;

/// <summary>
/// Creates and caches the per-user self-signed certificate used to secure the
/// local HTTPS receiver. The certificate's SHA-256 hash is published as the
/// device "fingerprint", which peers use to pin the connection.
/// </summary>
public static class CertificateManager
{
    // Local-only PFX protection. The key never leaves the user profile, so a
    // fixed password is acceptable here (this mirrors the reference clients,
    // which also ship a build-time PKCS#12 password).
    private const string PfxPassword = "localsend-win-local";

    private const X509KeyStorageFlags Flags =
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet;

    /// <summary>Loads the certificate from <paramref name="pfxPath"/>, creating it on first run.</summary>
    public static X509Certificate2 GetOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                return new X509Certificate2(pfxPath, PfxPassword, Flags);
            }
            catch (CryptographicException)
            {
                // Corrupt or unreadable store entry — regenerate below.
                TryDelete(pfxPath);
            }
        }

        var cert = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, PfxPassword));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, PfxPassword), PfxPassword, Flags);
    }

    private static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=LocalSendWin", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localsend");
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    /// <summary>
    /// SHA-256 of the certificate DER, rendered in the colon-separated upper-case hex
    /// form (<c>AB:CD:…</c>) that the official LocalSend clients advertise, so that our
    /// fingerprint is directly comparable with theirs.
    /// </summary>
    public static string GetFingerprint(X509Certificate2 cert)
    {
        var hex = Convert.ToHexString(SHA256.HashData(cert.RawData));
        return string.Join(":", Enumerable.Range(0, hex.Length / 2)
            .Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>
    /// Canonical form used when comparing fingerprints. Peers may spell the same hash as
    /// raw hex, colon-separated hex, or with different capitalisation; strip everything
    /// that is not a hex digit before comparing, otherwise a purely cosmetic difference
    /// makes certificate pinning reject a perfectly valid peer.
    /// </summary>
    public static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return string.Empty;

        var buffer = new char[fingerprint.Length];
        var length = 0;
        foreach (var c in fingerprint)
        {
            if (c == ':' || c == '-' || char.IsWhiteSpace(c)) continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, length);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
