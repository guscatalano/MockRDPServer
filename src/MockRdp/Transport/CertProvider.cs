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
    public static X509Certificate2 CreateSelfSigned(string commonName = "mock-rdp")
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

        // Round-trip through PKCS#12 so the private key is usable by SslStream on Windows.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }
}
