using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteDesktop.Core;

namespace RemoteDesktop.Security;

// Wire format: [12-byte nonce][16-byte GCM tag][ciphertext]
public sealed class AesGcmSessionCrypto : ISessionCrypto, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly ConcurrentDictionary<SessionId, byte[]> _sessionKeys = new();

    public Task DeriveSessionKeyAsync(
        SessionId sessionId,
        byte[] localEcDsaPrivateKey,
        byte[] remoteSubjectPublicKeyInfoDer,
        CancellationToken ct = default) =>
        DeriveSessionKeyAsync(sessionId, localEcDsaPrivateKey, remoteSubjectPublicKeyInfoDer, ReadOnlyMemory<byte>.Empty, ct);

    public Task DeriveSessionKeyAsync(
        SessionId sessionId,
        byte[] localEcDsaPrivateKey,
        byte[] remoteSubjectPublicKeyInfoDer,
        ReadOnlyMemory<byte> context,
        CancellationToken ct = default)
    {
        // Import ECDsa private key parameters into an ECDiffieHellman key on the same P-256 curve
        using var localEcdsa = ECDsa.Create();
        localEcdsa.ImportECPrivateKey(localEcDsaPrivateKey, out _);
        var localParams = localEcdsa.ExportParameters(includePrivateParameters: true);

        using var localEcdh = ECDiffieHellman.Create(localParams);

        using var remoteEcdsa = ECDsa.Create();
        remoteEcdsa.ImportSubjectPublicKeyInfo(remoteSubjectPublicKeyInfoDer, out _);
        var remoteParams = remoteEcdsa.ExportParameters(includePrivateParameters: false);

        using var remoteEcdh = ECDiffieHellman.Create(remoteParams);

        // Raw shared secret (32 bytes for P-256)
        var sharedSecret = localEcdh.DeriveKeyMaterial(remoteEcdh.PublicKey);

        // HKDF-expand into a 256-bit AES key. The info binds the SessionId and the caller's context (the key epoch),
        // so each epoch gets an independent key: frames captured under an earlier epoch cannot authenticate under a later
        // one, which is what stops replay of old traffic into a reconnected session. Empty context = the original derivation.
        var info = new byte[16 + context.Length];
        sessionId.Value.TryWriteBytes(info);
        context.Span.CopyTo(info.AsSpan(16));
        var sessionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            KeySize,
            salt: null,
            info: info);

        CryptographicOperations.ZeroMemory(sharedSecret);

        // Replacing a key (new epoch) must not leave the old one in memory.
        if (_sessionKeys.TryGetValue(sessionId, out var previous)) CryptographicOperations.ZeroMemory(previous);
        _sessionKeys[sessionId] = sessionKey;
        return Task.CompletedTask;
    }

    public Task<byte[]> EncryptAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken ct = default) =>
        EncryptAsync(sessionId, plaintext, ReadOnlyMemory<byte>.Empty, ct);

    public Task<byte[]> DecryptAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken ct = default) =>
        DecryptAsync(sessionId, ciphertext, ReadOnlyMemory<byte>.Empty, ct);

    public Task<byte[]> EncryptAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken ct = default)
    {
        var result = new byte[plaintext.Length + Overhead];
        EncryptTo(sessionId, plaintext.Span, associatedData.Span, result);
        return Task.FromResult(result);
    }

    public int Overhead => NonceSize + TagSize;

    // Encrypts straight into the caller's buffer: [nonce][tag][ciphertext]. No intermediate arrays.
    public int EncryptTo(SessionId sessionId, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData,
        Span<byte> destination)
    {
        if (destination.Length != plaintext.Length + Overhead)
            throw new ArgumentException("Destination must be exactly plaintext length + Overhead.", nameof(destination));

        var key = GetKey(sessionId);
        var nonce = destination[..NonceSize];
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, destination[(NonceSize + TagSize)..], destination.Slice(NonceSize, TagSize), associatedData);
        return destination.Length;
    }

    public Task<byte[]> DecryptAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken ct = default)
    {
        if (ciphertext.Length < NonceSize + TagSize)
            throw new ArgumentException("Ciphertext is too short.", nameof(ciphertext));

        var key = GetKey(sessionId);
        var data = ciphertext.Span;

        var nonce = data[..NonceSize];
        var tag = data[NonceSize..(NonceSize + TagSize)];
        var encrypted = data[(NonceSize + TagSize)..];

        var plaintext = new byte[encrypted.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, encrypted, tag, plaintext, associatedData.Span);

        return Task.FromResult(plaintext);
    }

    public Task DisposeSessionKeyAsync(SessionId sessionId, CancellationToken ct = default)
    {
        if (_sessionKeys.TryRemove(sessionId, out var key))
            CryptographicOperations.ZeroMemory(key);
        return Task.CompletedTask;
    }

    private byte[] GetKey(SessionId sessionId) =>
        _sessionKeys.TryGetValue(sessionId, out var key)
            ? key
            : throw new InvalidOperationException($"No session key for session {sessionId}. Call DeriveSessionKeyAsync first.");

    public void Dispose()
    {
        foreach (var key in _sessionKeys.Values)
            CryptographicOperations.ZeroMemory(key);
        _sessionKeys.Clear();
    }
}
