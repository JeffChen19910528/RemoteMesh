namespace RemoteDesktop.Core;

public sealed class DeviceInfo
{
    public DeviceId DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Platform { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public PresenceStatus Presence { get; set; } = PresenceStatus.Offline;
}
