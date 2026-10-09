using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Security;

namespace RemoteDesktop.Relay;

/// <summary>
/// Puts TLS (the platform's TLS stack: SChannel, OpenSSL, Secure Transport; nothing of ours) between the two devices,
/// through the relay. Both sides authenticate with their device key, wrapped in a throw-away self-signed certificate,
/// and each accepts only the exact public key it pinned for the other side. The key exchange is ephemeral, so
/// recordings of the traffic stay unreadable even if a device key leaks later (forward secrecy). The relay carries
/// the handshake like any other bytes and cannot take part in it.
/// </summary>
public static class RelayTls
{
    /// <summary>The name used in the handshake. It identifies nothing: the pinned public key is what counts.</summary>
    internal const string PeerName = "remotemesh-device";

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs the handshake as TLS client (<paramref name="initiator"/>) or server over <paramref name="socket"/> and
    /// returns a WebSocket that carries everything, both ways, inside TLS. Throws if the other end does not prove it
    /// holds the pinned key; the socket is then closed.
    /// </summary>
    public static async Task<WebSocket> AuthenticateAsync(
        WebSocket socket, bool initiator, DeviceKeyPair mine, byte[] pinnedPeerPublicKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(pinnedPeerPublicKey);

        var inner = new WebSocketByteStream(socket);
        SslStream? ssl = null;
        X509Certificate2? certificate = null;
        try
        {
            certificate = CreateCertificate(mine);
            ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, remote, _, errors) => AcceptsPinnedKey(remote, pinnedPeerPublicKey));

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(HandshakeTimeout);

            if (initiator)
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = PeerName,
                    ClientCertificates = new X509CertificateCollection { certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption
                }, limit.Token);
            }
            else
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true, // both sides prove who they are
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption
                }, limit.Token);
            }

            if (!IsForwardSecret(ssl)) throw new AuthenticationException("The connection did not negotiate an ephemeral key exchange.");
            return new TlsWebSocket(ssl, socket, certificate);
        }
        catch
        {
            // A handshake that failed leaves nothing open: the other end must see the connection end, not hang.
            try { if (ssl is not null) await ssl.DisposeAsync(); else await inner.DisposeAsync(); } catch (Exception) { }
            try { socket.Abort(); } catch (Exception) { }
            certificate?.Dispose();
            throw;
        }
    }

    /// <summary>The negotiated protocol and cipher suite of a connection returned by <see cref="AuthenticateAsync"/>, or null for any other socket.</summary>
    public static string? Describe(WebSocket socket) => (socket as TlsWebSocket)?.Description;

    /// <summary>
    /// True when the negotiated TLS version/cipher uses an ephemeral key exchange: always for TLS 1.3, and for TLS 1.2
    /// the ECDHE suites (an ECDSA certificate cannot be used with anything else).
    /// </summary>
    internal static bool IsForwardSecret(SslStream ssl) =>
        ssl.SslProtocol == SslProtocols.Tls13
        || (ssl.SslProtocol == SslProtocols.Tls12 && ssl.NegotiatedCipherSuite.ToString().Contains("ECDHE", StringComparison.Ordinal));

    private static bool AcceptsPinnedKey(X509Certificate? remote, byte[] pinned)
    {
        if (remote is null) return false;
        try
        {
            using var cert = new X509Certificate2(remote);
            var presented = cert.PublicKey.ExportSubjectPublicKeyInfo();
            // The certificate is only a carrier. Chain and name errors are expected (self-signed) and irrelevant: the
            // question is whether this is the key that was pinned, and the handshake proves the peer holds its private half.
            return CryptographicOperations.FixedTimeEquals(presented, pinned);
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>A short-lived self-signed certificate around the device key (the key itself never leaves this process).</summary>
    internal static X509Certificate2 CreateCertificate(DeviceKeyPair keys)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(keys.PrivateKey, out _);
        var request = new CertificateRequest("CN=" + PeerName, ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // server and client authentication
        // Wide validity window: the two clocks do not have to agree, and expiry is not what protects anything here.
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        // Re-import so the platform's TLS stack can use the key.
        //   Windows SChannel:     DefaultKeySet — the key goes into a user-profile container; .NET deletes it when the certificate is disposed.
        //   macOS Secure Transport: DefaultKeySet — macOS does not support EphemeralKeySet; the key is held in a temporary Keychain item and removed on dispose.
        //   Linux OpenSSL:        EphemeralKeySet — keeps the key in memory only, never written to disk.
        var flags = OperatingSystem.IsLinux() ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.DefaultKeySet;
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null, flags);
    }
}
