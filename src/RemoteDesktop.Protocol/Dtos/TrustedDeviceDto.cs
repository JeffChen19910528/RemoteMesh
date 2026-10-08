namespace RemoteDesktop.Protocol.Dtos;

public sealed class TrustedDeviceDto
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public DateTimeOffset TrustedSince { get; set; }
    public long DefaultPermissions { get; set; }
}
