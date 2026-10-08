namespace RemoteDesktop.Core;

public sealed class DeviceIdentity
{
    public DeviceId DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Platform { get; init; }
    public required byte[] PublicKey { get; init; }  // ECDsa SubjectPublicKeyInfo (DER)
    public DateTimeOffset CreatedAt { get; init; }

    public DeviceInfo ToDeviceInfo() => new()
    {
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        Platform = Platform,
        CreatedAt = CreatedAt
    };

    public static string DetectPlatform()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Unknown";
    }
}
