using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Minecraft;

/// <summary>
/// Owns the short-lived certificate used by the peer diagnostics transport.
/// The key is generated for this process only and is never written to a store.
/// </summary>
internal sealed class PeerSupportCertificate : IDisposable
{
    private const int FingerprintByteLength = 32;
    private readonly ECDsa _privateKey;
    private int _disposed;

    private PeerSupportCertificate(ECDsa privateKey, X509Certificate2 certificate)
    {
        _privateKey = privateKey;
        Certificate = certificate;
        Fingerprint = GetFingerprint(certificate);
    }

    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }

    public static PeerSupportCertificate CreateEphemeral(DateTimeOffset? now = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var request = new CertificateRequest(
                "CN=Minecraft Portable Diagnostics",
                key,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(
                    certificateAuthority: false,
                    hasPathLengthConstraint: false,
                    pathLengthConstraint: 0,
                    critical: true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature,
                    critical: true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    [
                        new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication"),
                        new Oid("1.3.6.1.5.5.7.3.2", "Client Authentication")
                    ],
                    critical: true));
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

            using var generatedCertificate = request.CreateSelfSigned(
                issuedAt.AddMinutes(-5),
                issuedAt.AddDays(1));
            var encoded = generatedCertificate.Export(X509ContentType.Pkcs12);
            try
            {
                var certificate = X509CertificateLoader.LoadPkcs12(
                    encoded,
                    password: null,
                    OperatingSystem.IsWindows()
                        ? X509KeyStorageFlags.UserKeySet |
                          X509KeyStorageFlags.Exportable
                        : X509KeyStorageFlags.EphemeralKeySet |
                    X509KeyStorageFlags.Exportable);
                return new PeerSupportCertificate(key, certificate);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static string GetFingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    public static bool TryNormalizeFingerprint(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<char> fingerprint = stackalloc char[FingerprintByteLength * 2];
        var written = 0;
        foreach (var character in value)
        {
            if (character is ':' or '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            if (!Uri.IsHexDigit(character) || written >= fingerprint.Length)
            {
                return false;
            }

            fingerprint[written++] = char.ToUpperInvariant(character);
        }

        if (written != fingerprint.Length)
        {
            return false;
        }

        normalized = new string(fingerprint);
        return true;
    }

    public static bool MatchesFingerprint(
        X509Certificate? certificate,
        string? expectedFingerprint)
    {
        if (certificate is null ||
            !TryNormalizeFingerprint(expectedFingerprint, out var normalized))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(normalized);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(certificate.GetRawCertData());
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Certificate.Dispose();
        _privateKey.Dispose();
    }
}

internal sealed record PeerSupportTlsConnection(
    SslStream Stream,
    string RemoteCertificateFingerprint) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

internal static class PeerSupportTls
{
    private const string TlsTargetName = "MinecraftPortableDiagnostics";
    private const SslProtocols AllowedProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;

    public static async Task<PeerSupportTlsConnection> AuthenticateAsClientAsync(
        Stream transport,
        PeerSupportCertificate localCertificate,
        string expectedRemoteFingerprint,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(localCertificate);
        if (!PeerSupportCertificate.TryNormalizeFingerprint(
                expectedRemoteFingerprint,
                out var expected))
        {
            throw new ArgumentException(
                "The expected diagnostics certificate fingerprint is invalid.",
                nameof(expectedRemoteFingerprint));
        }

        string? observedFingerprint = null;
        var ssl = new SslStream(
            transport,
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                if (!PeerSupportCertificate.MatchesFingerprint(certificate, expected))
                {
                    return false;
                }

                observedFingerprint = PeerSupportCertificate.GetFingerprint(certificate!);
                return true;
            });

        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = TlsTargetName,
                    ClientCertificates = new X509CertificateCollection
                    {
                        localCertificate.Certificate
                    },
                    EnabledSslProtocols = AllowedProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(observedFingerprint))
            {
                throw new AuthenticationException(
                    "The diagnostics peer did not present its expected certificate.");
            }

            return new PeerSupportTlsConnection(ssl, observedFingerprint);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static Task<PeerSupportTlsConnection> AuthenticateAsServerAsync(
        Stream transport,
        PeerSupportCertificate localCertificate,
        string expectedRemoteFingerprint,
        CancellationToken token)
    {
        if (!PeerSupportCertificate.TryNormalizeFingerprint(
                expectedRemoteFingerprint,
                out var expected))
        {
            throw new ArgumentException(
                "The expected diagnostics certificate fingerprint is invalid.",
                nameof(expectedRemoteFingerprint));
        }

        return AuthenticateAsServerAsync(
            transport,
            localCertificate,
            fingerprint => string.Equals(
                fingerprint,
                expected,
                StringComparison.Ordinal),
            token);
    }

    public static async Task<PeerSupportTlsConnection> AuthenticateAsServerAsync(
        Stream transport,
        PeerSupportCertificate localCertificate,
        Func<string, bool> isExpectedRemoteFingerprint,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(isExpectedRemoteFingerprint);

        string? observedFingerprint = null;
        var ssl = new SslStream(
            transport,
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                var fingerprint = PeerSupportCertificate.GetFingerprint(certificate);
                bool accepted;
                try
                {
                    accepted = isExpectedRemoteFingerprint(fingerprint);
                }
                catch
                {
                    accepted = false;
                }

                if (!accepted)
                {
                    return false;
                }

                observedFingerprint = fingerprint;
                return true;
            });

        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = localCertificate.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = AllowedProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(observedFingerprint))
            {
                throw new AuthenticationException(
                    "The diagnostics peer did not present an accepted client certificate.");
            }

            return new PeerSupportTlsConnection(ssl, observedFingerprint);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
