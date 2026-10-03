using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodexPhoneReminder.Agent;

public sealed class TransportIdentity
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    public TransportIdentity(IWebHostEnvironment environment, bool persistKey = true)
    {
        var data = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "transport-identity-v4.pfx");
        var passwordPath = Path.Combine(data, "transport-identity-v4.password");
        if (!File.Exists(path) || !File.Exists(passwordPath)) Create(path, passwordPath);
        var password = File.ReadAllText(passwordPath);
        var flags = persistKey
            ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
        Fingerprint = Convert.ToHexString(SHA256.HashData(Certificate.RawData));
    }

    private static void Create(string path, string passwordPath)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Codex Phone Reminder", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1", "TLS Web Server Authentication") }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(passwordPath, password);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
    }
}
