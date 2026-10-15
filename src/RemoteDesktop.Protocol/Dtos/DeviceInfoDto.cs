using RemoteDesktop.Core;

namespace RemoteDesktop.Protocol.Dtos;

public sealed class DeviceInfoDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string PublicKeyBase64 { get; set; } = string.Empty;

    public static DeviceInfoDto From(DeviceInfo d, byte[] publicKey) => new()
    {
        DeviceId = d.DeviceId.ToString(),
        DeviceName = d.DeviceName,
        Platform = d.Platform,
        PublicKeyBase64 = Convert.ToBase64String(publicKey)
    };

    public (DeviceInfo Device, byte[] PublicKey) ToDomain() => (
        new DeviceInfo
        {
            DeviceId = Core.DeviceId.Parse(DeviceId),
            DeviceName = DeviceName,
            Platform = Platform,
            CreatedAt = DateTimeOffset.UtcNow
        },
        Convert.FromBase64String(PublicKeyBase64)
    );
}
