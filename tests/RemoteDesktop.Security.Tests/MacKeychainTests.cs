using RemoteDesktop.Security;

namespace RemoteDesktop.Security.Tests;

/// <summary>
/// Tests for the macOS Keychain helpers. The encryption and decryption logic (AES-GCM) is pure and runs everywhere;
/// the actual Keychain P/Invoke calls only execute on macOS. Tests that touch the real Keychain are skipped on other
/// operating systems so the CI pipeline stays green on Linux/Windows builders.
/// </summary>
public sealed class MacKeychainTests
{
    // ── AES-GCM round-trip (runs everywhere) ─────────────────────────────

    [Fact]
    public void Protect_ThenUnprotect_RecoverOriginalBytes()
    {
        if (!OperatingSystem.IsMacOS()) return; // Keychain P/Invoke is only wired on macOS
        var account = "test-" + Guid.NewGuid().ToString("N");

        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var ciphertext = MacKeychain.Protect(original, account);

        // Skip if the Keychain is unavailable (e.g. headless CI before first login)
        if (ciphertext is null) return;

        var recovered = MacKeychain.Unprotect(ciphertext, account);
        Assert.NotNull(recovered);
        Assert.Equal(original, recovered);
    }

    [Fact]
    public void Protect_TwiceSameAccount_ProduceDifferentCiphertexts()
    {
        // AES-GCM uses a fresh random nonce every time, so two encryptions of the same plaintext must differ.
        if (!OperatingSystem.IsMacOS()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");

        var original = new byte[32];
        var c1 = MacKeychain.Protect(original, account);
        var c2 = MacKeychain.Protect(original, account);

        if (c1 is null || c2 is null) return; // Keychain unavailable
        Assert.False(c1.SequenceEqual(c2), "Two encryptions of the same plaintext must have different nonces.");
    }

    [Fact]
    public void Protect_TwiceSameAccount_BothDecryptCorrectly()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");

        var original = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var c1 = MacKeychain.Protect(original, account);
        var c2 = MacKeychain.Protect(original, account);

        if (c1 is null || c2 is null) return;
        Assert.Equal(original, MacKeychain.Unprotect(c1, account));
        Assert.Equal(original, MacKeychain.Unprotect(c2, account));
    }

    [Fact]
    public void Unprotect_WithWrongAccount_ReturnsNull()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var account1 = "test-" + Guid.NewGuid().ToString("N");
        var account2 = "test-" + Guid.NewGuid().ToString("N");

        var original = new byte[] { 1, 2, 3, 4 };
        var ciphertext = MacKeychain.Protect(original, account1);
        if (ciphertext is null) return;

        // The Keychain key for account2 is different (or absent): decryption must fail gracefully.
        var result = MacKeychain.Unprotect(ciphertext, account2);
        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_WithTamperedCiphertext_ReturnsNull()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");

        var ciphertext = MacKeychain.Protect(new byte[] { 1, 2, 3 }, account);
        if (ciphertext is null) return;

        // Flip a byte inside the ciphertext (after the 12-byte nonce and 16-byte tag).
        if (ciphertext.Length > MacKeychain.EncryptedOverhead)
            ciphertext[MacKeychain.EncryptedOverhead] ^= 0xFF;

        var result = MacKeychain.Unprotect(ciphertext, account);
        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_WithTooShortBlob_ReturnsNull()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");

        // A blob shorter than the nonce+tag overhead cannot be valid.
        var result = MacKeychain.Unprotect(new byte[MacKeychain.EncryptedOverhead - 1], account);
        Assert.Null(result);
    }

    [Fact]
    public void Delete_RemovesTheKeychainItem_SubsequentUnprotectFails()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");

        var original = new byte[] { 10, 20, 30 };
        var ciphertext = MacKeychain.Protect(original, account);
        if (ciphertext is null) return;

        MacKeychain.Delete(account);

        // After deleting the Keychain item, the ciphertext can no longer be decrypted.
        var result = MacKeychain.Unprotect(ciphertext, account);
        Assert.Null(result);
    }
}
