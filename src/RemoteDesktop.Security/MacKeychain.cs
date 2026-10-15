using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Security;

/// <summary>
/// Stores and retrieves a per-device file-encryption key from the macOS Keychain so that the private key material on
/// disk is encrypted at rest, not merely owner-readable. The strategy mirrors Windows DPAPI: a random AES-256 key is
/// sealed inside the Keychain under the logged-in user's login keychain; the file on disk contains the ciphertext.
/// If the Keychain is unreachable (e.g. headless CI, before first login), the caller falls back to no file encryption.
/// </summary>
internal static class MacKeychain
{
    // errSecSuccess = 0, errSecItemNotFound = -25300, errSecDuplicateItem = -25299
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    private const string ServiceName = "RemoteDesktop.OsKeyStore.v1";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string serviceName,
        uint accountNameLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string accountName,
        uint passwordLength, byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string serviceName,
        uint accountNameLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string accountName,
        out uint passwordLength, out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint dataLength, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    /// <summary>The nonce (12 bytes) + tag (16 bytes) overhead that <see cref="Encrypt"/> prepends and <see cref="Decrypt"/> strips.</summary>
    internal const int EncryptedOverhead = 12 + 16;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with a Keychain-stored AES-256-GCM key identified by
    /// <paramref name="account"/>, creating the Keychain item on first call. Returns null when the Keychain is
    /// unavailable (the caller should fall back to plaintext + file-permission protection).
    /// </summary>
    internal static byte[]? Protect(byte[] plaintext, string account)
    {
        var fileKey = GetOrCreateKey(account);
        if (fileKey is null) return null;
        return Encrypt(plaintext, fileKey);
    }

    /// <summary>
    /// Decrypts a blob produced by <see cref="Protect"/>. Returns null when the Keychain key is unavailable or the
    /// ciphertext is not in the expected format (the caller should treat the blob as a legacy plaintext key instead).
    /// </summary>
    internal static byte[]? Unprotect(byte[] ciphertext, string account)
    {
        if (ciphertext.Length < EncryptedOverhead) return null;
        var fileKey = FindKey(account);
        if (fileKey is null) return null;
        try { return Decrypt(ciphertext, fileKey); }
        catch (AuthenticationTagMismatchException) { return null; }
        catch (CryptographicException) { return null; }
    }

    /// <summary>
    /// Moves a Keychain item from <paramref name="oldAccount"/> to <paramref name="newAccount"/> so that the account
    /// name stays in sync after a file rename (e.g. when a pending key is committed to replace the main key).
    /// Silently succeeds when no item exists under <paramref name="oldAccount"/>.
    /// </summary>
    internal static void Rename(string oldAccount, string newAccount)
    {
        var key = FindKey(oldAccount);
        if (key is null) return;

        // Overwrite any existing item for newAccount, then delete the old one.
        if (!TrySaveKey(newAccount, key))
        {
            // Could not save under the new name: leave the old item so the key is not lost.
            CryptographicOperations.ZeroMemory(key);
            return;
        }
        CryptographicOperations.ZeroMemory(key);
        Delete(oldAccount);
    }

    /// <summary>Removes the Keychain item for <paramref name="account"/>. Silently succeeds when no item exists.</summary>
    internal static void Delete(string account)
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Encoding.UTF8.GetByteCount(ServiceName), ServiceName,
            (uint)Encoding.UTF8.GetByteCount(account), account,
            out _, out var data, out var item);
        if (status != ErrSecSuccess) return;
        try
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
            {
                SecKeychainItemDelete(item);
                CFRelease(item);
            }
        }
        catch (Exception) { }
    }

    private static byte[]? GetOrCreateKey(string account)
    {
        var existing = FindKey(account);
        if (existing is not null) return existing;

        var newKey = RandomNumberGenerator.GetBytes(32);
        if (!TrySaveKey(account, newKey)) return null;
        return newKey;
    }

    private static byte[]? FindKey(string account)
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Encoding.UTF8.GetByteCount(ServiceName), ServiceName,
            (uint)Encoding.UTF8.GetByteCount(account), account,
            out var len, out var data, out var item);

        if (status == ErrSecItemNotFound) return null;
        if (status != ErrSecSuccess) return null;

        try
        {
            if (len != 32) return null; // wrong size means a corrupt or foreign item
            var key = new byte[32];
            Marshal.Copy(data, key, 0, 32);
            return key;
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    private static bool TrySaveKey(string account, byte[] key)
    {
        var status = SecKeychainAddGenericPassword(
            IntPtr.Zero,
            (uint)Encoding.UTF8.GetByteCount(ServiceName), ServiceName,
            (uint)Encoding.UTF8.GetByteCount(account), account,
            (uint)key.Length, key,
            out var item);

        if (item != IntPtr.Zero) CFRelease(item);

        if (status == ErrSecDuplicateItem)
            return TryUpdateKey(account, key);

        return status == ErrSecSuccess;
    }

    private static bool TryUpdateKey(string account, byte[] key)
    {
        // Find the existing item and update its password data in place.
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Encoding.UTF8.GetByteCount(ServiceName), ServiceName,
            (uint)Encoding.UTF8.GetByteCount(account), account,
            out _, out var data, out var item);
        if (status != ErrSecSuccess) return false;

        SecKeychainItemFreeContent(IntPtr.Zero, data);
        if (item == IntPtr.Zero) return false;
        try
        {
            return SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)key.Length, key) == ErrSecSuccess;
        }
        finally { CFRelease(item); }
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, 12);
        Buffer.BlockCopy(tag, 0, result, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);
        return result;
    }

    private static byte[] Decrypt(byte[] blob, byte[] key)
    {
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var ciphertext = blob.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
