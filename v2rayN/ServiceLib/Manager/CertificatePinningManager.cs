using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ServiceLib.Manager;

public static class CertificatePinningManager
{
    private static readonly string _tag = nameof(CertificatePinningManager);
    private static readonly TimeSpan _tlsProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, List<string>> _singboxPublicKeyPinCache = new();

    public static List<string> ResolveSingboxPublicKeyPins(string address, int port, string? serverName, string? certSha)
    {
        var certPins = ParseCertificateSha256Pins(certSha);
        if (address.IsNullOrEmpty() || port <= 0 || certPins.Count == 0)
        {
            return [];
        }

        var targetHost = serverName.IsNotEmpty() ? serverName.TrimEx() : address.TrimEx();
        var cacheKey = $"{address.TrimEx()}:{port}|{targetHost}|{string.Join(",", certPins)}";
        if (_singboxPublicKeyPinCache.TryGetValue(cacheKey, out var cachedPins))
        {
            return [.. cachedPins];
        }

        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(address, port);
            if (!connectTask.Wait(_tlsProbeTimeout))
            {
                return [];
            }

            using var sslStream = new SslStream(
                tcpClient.GetStream(),
                false,
                static (_, _, _, _) => true);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            var authTask = sslStream.AuthenticateAsClientAsync(options);
            if (!authTask.Wait(_tlsProbeTimeout) || sslStream.RemoteCertificate is null)
            {
                return [];
            }

            using var cert = X509CertificateLoader.LoadCertificate(
                sslStream.RemoteCertificate.Export(X509ContentType.Cert));
            if (!TryGetSingboxPublicKeyPinFromPinnedCertificate(cert, certPins, out var publicKeyPin))
            {
                return [];
            }

            var resolvedPins = new List<string> { publicKeyPin };
            _singboxPublicKeyPinCache.TryAdd(cacheKey, resolvedPins);
            return [.. resolvedPins];
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return [];
        }
    }

    public static bool TryGetSingboxPublicKeyPinFromPinnedCertificate(string pemCert, string? certSha, out string publicKeyPin)
    {
        publicKeyPin = string.Empty;

        var certPins = ParseCertificateSha256Pins(certSha);
        if (pemCert.IsNullOrEmpty() || certPins.Count == 0)
        {
            return false;
        }

        try
        {
            using var cert = X509Certificate2.CreateFromPem(pemCert);
            return TryGetSingboxPublicKeyPinFromPinnedCertificate(cert, certPins, out publicKeyPin);
        }
        catch
        {
            return false;
        }
    }

    public static List<string> ParseCertificateSha256Pins(string? certSha)
    {
        return (Utils.String2List(certSha) ?? [])
            .Select(NormalizeCertificateSha256Pin)
            .Where(static pin => pin.Length == 64 && pin.All(Uri.IsHexDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryGetSingboxPublicKeyPinFromPinnedCertificate(
        X509Certificate2 cert,
        List<string> certPins,
        out string publicKeyPin)
    {
        publicKeyPin = string.Empty;

        var actualCertPin = Convert.ToHexString(SHA256.HashData(cert.RawData));
        if (!certPins.Contains(actualCertPin, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        publicKeyPin = Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
        return publicKeyPin.IsNotEmpty();
    }

    private static string NormalizeCertificateSha256Pin(string pin)
    {
        pin = pin.TrimEx();
        if (pin.StartsWith("sha256/", StringComparison.OrdinalIgnoreCase))
        {
            pin = pin["sha256/".Length..];
        }

        return pin.Replace(":", string.Empty).TrimEx();
    }
}
