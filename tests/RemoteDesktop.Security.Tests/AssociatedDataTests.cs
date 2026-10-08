using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Security;

namespace RemoteDesktop.Security.Tests;

public sealed class AssociatedDataTests : IDisposable
{
    private readonly AesGcmSessionCrypto _crypto = new();
    private readonly SessionId _session = SessionId.New();

    public AssociatedDataTests()
    {
        var a = DeviceKeyGenerator.Generate();
        var b = DeviceKeyGenerator.Generate();
        _crypto.DeriveSessionKeyAsync(_session, a.PrivateKey, b.PublicKey).GetAwaiter().GetResult();
    }

    public void Dispose() => _crypto.Dispose();

    [Fact]
    public async Task MatchingAssociatedData_RoundTrips()
    {
        var aad = new byte[] { 1, 2, 3 };
        var ct = await _crypto.EncryptAsync(_session, "hello"u8.ToArray(), aad);
        Assert.Equal("hello"u8.ToArray(), await _crypto.DecryptAsync(_session, ct, aad));
    }

    [Fact]
    public async Task DifferentAssociatedData_FailsAuthentication()
    {
        var ct = await _crypto.EncryptAsync(_session, "hello"u8.ToArray(), new byte[] { 1, 2, 3 });

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => _crypto.DecryptAsync(_session, ct, new byte[] { 1, 2, 4 }));
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => _crypto.DecryptAsync(_session, ct, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task AssociatedData_IsNotPartOfTheCiphertext_ButTamperingStillFails()
    {
        var aad = "header"u8.ToArray();
        var ct = await _crypto.EncryptAsync(_session, "payload"u8.ToArray(), aad);
        Assert.DoesNotContain("header", System.Text.Encoding.UTF8.GetString(ct));

        ct[^1] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => _crypto.DecryptAsync(_session, ct, aad));
    }

    [Fact]
    public async Task LegacyOverloads_StillWork_AndMeanEmptyAssociatedData()
    {
        var ct = await _crypto.EncryptAsync(_session, "x"u8.ToArray());
        Assert.Equal("x"u8.ToArray(), await _crypto.DecryptAsync(_session, ct, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task ManyEncryptions_NeverReuseANonce()
    {
        var nonces = new HashSet<string>();
        for (var i = 0; i < 5000; i++)
        {
            var ct = await _crypto.EncryptAsync(_session, new byte[] { 1 });
            Assert.True(nonces.Add(Convert.ToHexString(ct.AsSpan(0, 12))), "nonce repeated");
        }
    }
}
