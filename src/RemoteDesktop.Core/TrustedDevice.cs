namespace RemoteDesktop.Core;

public sealed class TrustedDevice
{
    public required DeviceId OwnerDeviceId { get; init; }
    public required DeviceId TrustedDeviceId { get; init; }
    public string TrustedDeviceName { get; init; } = string.Empty;
    public DateTimeOffset TrustedSince { get; init; } = DateTimeOffset.UtcNow;
    public Permission DefaultPermissions { get; init; } = Permission.ViewOnly;
}
