using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesktop.Core;

namespace RemoteDesktop.Security;

public sealed class OsKeyStore : IKeyStore
{
    private readonly string _baseDirectory;

    public OsKeyStore(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? GetDefaultDirectory();
        Directory.CreateDirectory(_baseDirectory);
    }

    public async Task<DeviceKeyPair> LoadOrCreateAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        var path = KeyPath(deviceId);
        if (File.Exists(path))
            return await LoadAsync(path, ct);

        var keyPair = DeviceKeyGenerator.Generate();
        await SaveAsync(path, keyPair, ct);
        return keyPair;
    }

    public async Task<byte[]> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        var keyPair = await LoadOrCreateAsync(deviceId, ct);
        return keyPair.PublicKey;
    }

    public Task DeleteAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        var path = KeyPath(deviceId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task StagePendingAsync(DeviceId deviceId, DeviceKeyPair next, CancellationToken ct = default) =>
        await SaveAsync(PendingPath(deviceId), next, ct);

    public async Task<DeviceKeyPair?> LoadPendingAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        var path = PendingPath(deviceId);
        if (!File.Exists(path)) return null;
        try { return await LoadAsync(path, ct); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or FormatException or CryptographicException)
        {
            return null; // a half-written staging file is as good as none
        }
    }

    public Task CommitPendingAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        File.Move(PendingPath(deviceId), KeyPath(deviceId), overwrite: true); // one step: the old key is gone only when the new one is in place
        return Task.CompletedTask;
    }

    public Task DiscardPendingAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        var path = PendingPath(deviceId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private async Task<DeviceKeyPair> LoadAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct);
        var stored = JsonSerializer.Deserialize<StoredKeyFile>(json)
            ?? throw new InvalidDataException("Key file is corrupt.");

        var encryptedPrivateKey = Convert.FromBase64String(stored.EncryptedPrivateKey);
        var privateKey = UnprotectKey(encryptedPrivateKey);
        var publicKey = Convert.FromBase64String(stored.PublicKey);

        return new DeviceKeyPair
        {
            PublicKey = publicKey,
            PrivateKey = privateKey,
            CreatedAt = stored.CreatedAt
        };
    }

    private async Task SaveAsync(string path, DeviceKeyPair keyPair, CancellationToken ct)
    {
        var encryptedPrivateKey = ProtectKey(keyPair.PrivateKey);
        var stored = new StoredKeyFile
        {
            EncryptedPrivateKey = Convert.ToBase64String(encryptedPrivateKey),
            PublicKey = Convert.ToBase64String(keyPair.PublicKey),
            CreatedAt = keyPair.CreatedAt
        };
        var json = JsonSerializer.Serialize(stored);

        // Create the file already owner-only. Writing first and restricting afterwards would leave the private key
        // readable under the default umask for a moment.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(path, options))
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(json.AsMemory(), ct);

        RestrictFilePermissions(path); // also covers a pre-existing file that was overwritten
    }

    private static byte[] ProtectKey(byte[] privateKey)
    {
        if (OperatingSystem.IsWindows())
        {
            return System.Security.Cryptography.ProtectedData.Protect(
                privateKey, null, DataProtectionScope.CurrentUser);
        }
        // Linux: file permissions (600) provide OS-level isolation.
        // libsecret / kernel keyring integration is not implemented yet.
        return privateKey;
    }

    private static byte[] UnprotectKey(byte[] stored)
    {
        if (OperatingSystem.IsWindows())
        {
            return System.Security.Cryptography.ProtectedData.Unprotect(
                stored, null, DataProtectionScope.CurrentUser);
        }
        return stored;
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Owner read+write only — no group or world access
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private string PendingPath(DeviceId deviceId) =>
        Path.Combine(_baseDirectory, $"{deviceId}.key.next");

    private string KeyPath(DeviceId deviceId) =>
        Path.Combine(_baseDirectory, $"{deviceId}.key");

    private static string GetDefaultDirectory()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "RemoteDesktop", "devices");
    }

    private sealed class StoredKeyFile
    {
        public string EncryptedPrivateKey { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
