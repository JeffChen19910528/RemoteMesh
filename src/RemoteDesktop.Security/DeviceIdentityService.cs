using System.Text.Json;
using RemoteDesktop.Core;

namespace RemoteDesktop.Security;

// Coordinates device identity: stable DeviceId + key pair tied together in one file.
public sealed class DeviceIdentityService
{
    private readonly IKeyStore _keyStore;
    private readonly string _identityFilePath;

    public DeviceIdentityService(IKeyStore keyStore, string? baseDirectory = null)
    {
        _keyStore = keyStore;
        var dir = baseDirectory ?? GetDefaultDirectory();
        Directory.CreateDirectory(dir);
        _identityFilePath = Path.Combine(dir, "device.identity");
    }

    public async Task<DeviceIdentity> LoadOrCreateAsync(
        string deviceName,
        CancellationToken ct = default)
    {
        StoredIdentity? stored = null;

        if (File.Exists(_identityFilePath))
        {
            var json = await File.ReadAllTextAsync(_identityFilePath, ct);
            stored = JsonSerializer.Deserialize<StoredIdentity>(json);
        }

        DeviceId deviceId;
        if (stored is null || !DeviceId.TryParse(stored.DeviceId, out deviceId))
        {
            deviceId = DeviceId.New();
            stored = null; // force re-creation
        }

        // Load or create the key pair for this device ID
        var keyPair = await _keyStore.LoadOrCreateAsync(deviceId, ct);

        if (stored is null)
        {
            stored = new StoredIdentity
            {
                DeviceId = deviceId.ToString(),
                DeviceName = deviceName,
                Platform = DeviceIdentity.DetectPlatform(),
                CreatedAt = keyPair.CreatedAt
            };
            var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(_identityFilePath, json, ct);
        }

        return new DeviceIdentity
        {
            DeviceId = deviceId,
            DeviceName = stored.DeviceName,
            Platform = stored.Platform,
            PublicKey = keyPair.PublicKey,
            CreatedAt = stored.CreatedAt
        };
    }

    /// <summary>Where identity files live when no folder is given.</summary>
    public static string GetDefaultDirectory()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "RemoteDesktop");
    }

    private sealed class StoredIdentity
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
