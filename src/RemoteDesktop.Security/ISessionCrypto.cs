using RemoteDesktop.Core;

namespace RemoteDesktop.Security;

public interface ISessionCrypto
{
    Task<byte[]> EncryptAsync(SessionId sessionId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);
    Task<byte[]> DecryptAsync(SessionId sessionId, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default);

    /// <summary>Encrypts and authenticates <paramref name="associatedData"/> (not encrypted, but any change makes decryption fail).</summary>
    Task<byte[]> EncryptAsync(SessionId sessionId, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> associatedData,
        CancellationToken ct = default);

    /// <exception cref="System.Security.Cryptography.CryptographicException">Tampered data, wrong key or wrong associated data.</exception>
    Task<byte[]> DecryptAsync(SessionId sessionId, ReadOnlyMemory<byte> ciphertext, ReadOnlyMemory<byte> associatedData,
        CancellationToken ct = default);

    /// <summary>Bytes added to every message (nonce + tag).</summary>
    int Overhead { get; }

    /// <summary>
    /// Same as the associated-data EncryptAsync but writes <c>[nonce][tag][ciphertext]</c> into <paramref name="destination"/>
    /// (exactly plaintext length + <see cref="Overhead"/>), avoiding intermediate arrays on hot paths.
    /// </summary>
    int EncryptTo(SessionId sessionId, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData, Span<byte> destination);

    Task DeriveSessionKeyAsync(SessionId sessionId, byte[] localPrivateKey, byte[] remotePublicKey, CancellationToken ct = default);

    /// <summary>
    /// Derives the key for one connection epoch of the session. Both sides must pass the same <paramref name="context"/>
    /// (see <see cref="SessionKeyContext.ForEpoch"/>); a reconnect uses the next epoch so old frames cannot be replayed.
    /// </summary>
    Task DeriveSessionKeyAsync(SessionId sessionId, byte[] localPrivateKey, byte[] remotePublicKey,
        ReadOnlyMemory<byte> context, CancellationToken ct = default);
    Task DisposeSessionKeyAsync(SessionId sessionId, CancellationToken ct = default);
}
