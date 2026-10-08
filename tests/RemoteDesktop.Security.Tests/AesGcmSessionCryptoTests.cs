using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Security;

namespace RemoteDesktop.Security.Tests;

public sealed class AesGcmSessionCryptoTests : IDisposable
{
    private readonly AesGcmSessionCrypto _crypto = new();

    public void Dispose() => _crypto.Dispose();

    private static (DeviceKeyPair alice, DeviceKeyPair bob) MakeKeyPairs()
        => (DeviceKeyGenerator.Generate(), DeviceKeyGenerator.Generate());

    [Fact]
    public async Task DeriveSessionKey_ThenEncryptDecrypt_RoundTrips()
    {
        var (alice, bob) = MakeKeyPairs();
        var sessionId = SessionId.New();

        // Both sides derive same key via ECDH
        await _crypto.DeriveSessionKeyAsync(sessionId, alice.PrivateKey, bob.PublicKey);

        var plaintext = "Hello RemoteMesh!"u8.ToArray();
        var ciphertext = await _crypto.EncryptAsync(sessionId, plaintext);
        var decrypted = await _crypto.DecryptAsync(sessionId, ciphertext);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task BothSidesDeriveIdenticalKey()
    {
        var (alice, bob) = MakeKeyPairs();
        var sessionId = SessionId.New();

        using var cryptoAlice = new AesGcmSessionCrypto();
        using var cryptoBob = new AesGcmSessionCrypto();

        await cryptoAlice.DeriveSessionKeyAsync(sessionId, alice.PrivateKey, bob.PublicKey);
        await cryptoBob.DeriveSessionKeyAsync(sessionId, bob.PrivateKey, alice.PublicKey);

        var message = "cross-side encryption test"u8.ToArray();
        var ciphertext = await cryptoAlice.EncryptAsync(sessionId, message);
        var decrypted = await cryptoBob.DecryptAsync(sessionId, ciphertext);

        Assert.Equal(message, decrypted);
    }

    [Fact]
    public async Task Encrypt_ProducesUniqueCiphertextEachTime()
    {
        var (alice, bob) = MakeKeyPairs();
        var sessionId = SessionId.New();
        await _crypto.DeriveSessionKeyAsync(sessionId, alice.PrivateKey, bob.PublicKey);

        var plaintext = "same message"u8.ToArray();
        var c1 = await _crypto.EncryptAsync(sessionId, plaintext);
        var c2 = await _crypto.EncryptAsync(sessionId, plaintext);

        // Different nonces → different ciphertext
        Assert.False(c1.SequenceEqual(c2));
    }

    [Fact]
    public async Task Decrypt_ThrowsOnTamperedCiphertext()
    {
        var (alice, bob) = MakeKeyPairs();
        var sessionId = SessionId.New();
        await _crypto.DeriveSessionKeyAsync(sessionId, alice.PrivateKey, bob.PublicKey);

        var ciphertext = await _crypto.EncryptAsync(sessionId, "data"u8.ToArray());
        ciphertext[ciphertext.Length - 1] ^= 0xFF; // flip last byte

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => _crypto.DecryptAsync(sessionId, ciphertext));
    }

    [Fact]
    public async Task EncryptWithoutDeriveKey_Throws()
    {
        var unknownSession = SessionId.New();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _crypto.EncryptAsync(unknownSession, "data"u8.ToArray()));
    }

    [Fact]
    public async Task DisposeSessionKey_PreventsFurtherUse()
    {
        var (alice, bob) = MakeKeyPairs();
        var sessionId = SessionId.New();
        await _crypto.DeriveSessionKeyAsync(sessionId, alice.PrivateKey, bob.PublicKey);
        await _crypto.DisposeSessionKeyAsync(sessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _crypto.EncryptAsync(sessionId, "data"u8.ToArray()));
    }

    [Fact]
    public async Task Encrypt_EmptyPlaintext_Works()
    {
        var (alice, bob) = MakeKeyPairs();
        var sessionId = SessionId.New();
        await _crypto.DeriveSessionKeyAsync(sessionId, alice.PrivateKey, bob.PublicKey);

        var ciphertext = await _crypto.EncryptAsync(sessionId, Array.Empty<byte>());
        var decrypted = await _crypto.DecryptAsync(sessionId, ciphertext);
        Assert.Empty(decrypted);
    }
}
