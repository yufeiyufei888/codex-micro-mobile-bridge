using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodexMicroBridge.Core.Security;

public sealed record BridgeCertificate(
    X509Certificate2 Certificate,
    string CertificateSha256Fingerprint,
    string SpkiSha256Fingerprint,
    bool ReissuedForHostChange);

public sealed class BridgeCertificateProvider
{
    private const string SecretName = "bridge-tls.pfx.dpapi";
    private readonly DpapiSecretStore _secretStore;

    public BridgeCertificateProvider(DpapiSecretStore secretStore)
    {
        _secretStore = secretStore;
    }

    public BridgeCertificate GetOrCreate(string requiredHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredHost);
        var pfx = _secretStore.Load(SecretName);
        var reissued = false;
        if (pfx is null)
        {
            pfx = CreateCertificatePfx(requiredHost);
            _secretStore.Save(SecretName, pfx);
        }

        var certificate = Load(pfx);
        if (!certificate.MatchesHostname(requiredHost, allowWildcards: false, allowCommonName: false))
        {
            certificate.Dispose();
            pfx = CreateCertificatePfx(requiredHost);
            _secretStore.Save(SecretName, pfx);
            certificate = Load(pfx);
            reissued = true;
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CryptographicException("The bridge TLS certificate does not contain its private key.");
        }

        var certificateFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        using var publicKey = certificate.GetECDsaPublicKey()
            ?? throw new CryptographicException("The bridge TLS certificate does not contain an ECDSA public key.");
        var spkiFingerprint = Convert.ToHexString(SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo()));
        return new BridgeCertificate(
            certificate,
            FormatFingerprint(certificateFingerprint),
            FormatFingerprint(spkiFingerprint),
            reissued);
    }

    private static X509Certificate2 Load(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(
            pfx,
            password: null,
            X509KeyStorageFlags.Exportable |
            X509KeyStorageFlags.UserKeySet |
            X509KeyStorageFlags.PersistKeySet);

    private static byte[] CreateCertificatePfx(string requiredHost)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=Codex Micro Bridge",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var alternativeNames = new SubjectAlternativeNameBuilder();
        var dnsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            Dns.GetHostName(),
            $"{Dns.GetHostName()}.local",
        };
        var addresses = new HashSet<IPAddress>
        {
            IPAddress.Loopback,
            IPAddress.IPv6Loopback,
        };
        if (IPAddress.TryParse(requiredHost, out var requiredAddress))
        {
            addresses.Add(requiredAddress);
        }
        else
        {
            dnsNames.Add(requiredHost);
        }

        foreach (var address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            addresses.Add(address);
        }

        foreach (var name in dnsNames)
        {
            alternativeNames.AddDnsName(name);
        }

        foreach (var address in addresses)
        {
            alternativeNames.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(alternativeNames.Build());

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));
        return certificate.Export(X509ContentType.Pfx);
    }

    private static string FormatFingerprint(string fingerprint)
    {
        return string.Join(':', Enumerable.Range(0, fingerprint.Length / 2)
            .Select(index => fingerprint.Substring(index * 2, 2)));
    }
}
