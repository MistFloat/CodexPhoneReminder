using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodexPhoneReminder.Agent;

public sealed class TransportIdentity
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    public TransportIdentity(IWebHostEnvironment environment)
    {
        var data = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "transport-identity.pfx");
        if (!File.Exists(path)) Create(path);
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
        Fingerprint = Convert.ToHexString(SHA256.HashData(Certificate.RawData));
    }

    private static void Create(string path)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Codex Phone Reminder", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
    }
}
