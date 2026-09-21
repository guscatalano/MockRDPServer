using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MockRdp.Transport;

/// <summary>
/// Produces the TLS server certificate for the mock. A throwaway self-signed cert
/// is fine because test clients disable certificate validation (mstsc shows a
/// warning; FreeRDP takes /cert:ignore).
/// </summary>
public static class CertProvider
{
    /// <summary>Loads a persisted dev cert from <paramref name="pfxPath"/>, creating and saving one
    /// on first use. Stable across launches (same thumbprint), so a client can pin/trust it once.</summary>
    public static X509Certificate2 GetOrCreatePersistent(string pfxPath, string commonName = "mock-rdp")
    {
        if (File.Exists(pfxPath))
        {
            try { return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfxPath), null); }
            catch { /* unreadable/corrupt — regenerate below */ }
        }
        var pfx = CreateSelfSignedPfx(commonName);
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    public static X509Certificate2 CreateSelfSigned(string commonName = "mock-rdp") =>
        // Round-trip through PKCS#12 so the private key is usable by SslStream on Windows.
        X509CertificateLoader.LoadPkcs12(CreateSelfSignedPfx(commonName), null);

    private static byte[] CreateSelfSignedPfx(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return cert.Export(X509ContentType.Pfx);   // key is exportable here (before the round-trip)
    }
}
